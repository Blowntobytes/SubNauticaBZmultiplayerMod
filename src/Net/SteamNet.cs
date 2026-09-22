using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;
using BZMultiplayer.Sync;

namespace BZMultiplayer.Net
{
    /// <summary>
    /// Steam transport: one lobby per session, host-star topology over SteamNetworkingMessages
    /// (Steam Datagram Relay, no port forwarding). The game already calls SteamAPI.Init/RunCallbacks,
    /// so we only register callbacks and pump the message channel.
    /// </summary>
    public sealed class SteamNet
    {
        private const int Channel = 0;
        private const int SendUnreliable = 0;   // k_nSteamNetworkingSend_Unreliable
        private const int SendReliable = 8;     // k_nSteamNetworkingSend_Reliable
        private const string LobbyKeyVersion = "bzmp_version";
        private const string LobbyKeyMarker = "bzmp";

        private readonly PlayerRegistry players;
        private readonly PacketWriter writer = new PacketWriter();
        private readonly IntPtr[] recvPtrs = new IntPtr[64];
        private byte[] recvBuf = new byte[4096];
        private GCHandle sendPin;
        private byte[] sendPinned;

        private Callback<LobbyEnter_t> cbLobbyEnter;
        private Callback<LobbyChatUpdate_t> cbLobbyChat;
        private Callback<GameLobbyJoinRequested_t> cbJoinRequested;
        private Callback<SteamNetworkingMessagesSessionRequest_t> cbSessionRequest;
        private Callback<SteamNetworkingMessagesSessionFailed_t> cbSessionFailed;
        private CallResult<LobbyCreated_t> crLobbyCreated;

        private struct Queued { public ulong To; public byte[] Data; }
        private readonly Queue<Queued> reliableQueue = new Queue<Queued>();
        public int ReliableQueueLength { get { return reliableQueue.Count; } }
        public SaveSync Saves { get; private set; }

        private bool initialized;
        private CSteamID lobby = CSteamID.Nil;
        private CSteamID hostId = CSteamID.Nil;
        private CSteamID selfId = CSteamID.Nil;
        private readonly Dictionary<ulong, SteamNetworkingIdentity> identities = new Dictionary<ulong, SteamNetworkingIdentity>();
        private string status = "idle";

        public bool IsInSession { get { return lobby != CSteamID.Nil; } }
        public bool IsHost { get { return IsInSession && hostId == selfId; } }
        public ulong SelfId { get { return selfId.m_SteamID; } }
        public ulong LobbyId { get { return lobby.m_SteamID; } }
        public int LobbyMemberCount { get { return IsInSession ? SteamMatchmaking.GetNumLobbyMembers(lobby) : 0; } }
        public string HostName { get { return IsInSession && hostId != CSteamID.Nil ? SteamFriends.GetFriendPersonaName(hostId) : ""; } }
        public ulong HostSteamId { get { return hostId.m_SteamID; } }

        /// <summary>Join a lobby by its numeric id (Discord join secret, clipboard, command line).</summary>
        public bool JoinById(string text)
        {
            if (!initialized) return false;
            ulong id;
            if (!ulong.TryParse((text ?? "").Trim(), out id) || !new CSteamID(id).IsLobby()) return false;
            if (IsInSession && lobby.m_SteamID == id) return true;
            JoinWhenReady(new CSteamID(id));
            return true;
        }
        public string SelfName { get; private set; }

        public SteamNet(PlayerRegistry players)
        {
            this.players = players;
            Saves = new SaveSync(this);
        }

        public bool TryInit()
        {
            if (initialized) return true;
            try
            {
                selfId = SteamUser.GetSteamID();
                if (!selfId.IsValid()) return false;
                SelfName = SteamFriends.GetPersonaName();
            }
            catch (Exception)
            {
                return false; // Steamworks not initialised yet (game does it during boot)
            }

            SteamNetworkingUtils.InitRelayNetworkAccess();
            cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            cbLobbyChat = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            cbSessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            cbSessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);

            sendPinned = new byte[4096];
            sendPin = GCHandle.Alloc(sendPinned, GCHandleType.Pinned);
            initialized = true;
            Plugin.Log.LogInfo("Steam ready as " + SelfName + " (" + selfId.m_SteamID + ")");
            return true;
        }

        // ---------------------------------------------------------------- lobby lifecycle

        public void Host()
        {
            if (!initialized) return;
            if (IsInSession) { Plugin.Log.LogInfo("Already in a session."); return; }
            status = "creating lobby...";
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Mathf.Clamp(Plugin.MaxPlayers.Value, 2, 8));
            crLobbyCreated.Set(call);
        }

        public void OpenInvite()
        {
            if (!IsHost) { Plugin.Log.LogInfo("Not hosting; press " + Plugin.HostKey.Value + " first."); return; }
            bool overlay = SteamUtils.IsOverlayEnabled();
            Plugin.Log.LogInfo("Invite dialog requested. Steam overlay enabled: " + overlay + ". Lobby id " + lobby.m_SteamID + " copied to clipboard.");
            GUIUtility.systemCopyBuffer = lobby.m_SteamID.ToString();
            SteamFriends.ActivateGameOverlayInviteDialog(lobby);
            if (!overlay)
                Plugin.Log.LogWarning("Steam overlay is disabled for this game, so the invite dialog cannot appear. Friends can still join with " + Plugin.JoinFriendKey.Value + " (auto-find, or lobby id from clipboard).");
        }

        /// <summary>Overlay-free join: scan the friends list for someone hosting a Below Zero lobby and join it.</summary>
        public bool JoinFriend()
        {
            if (!initialized) return false;
            var appId = SteamUtils.GetAppID();
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                FriendGameInfo_t info;
                if (!SteamFriends.GetFriendGamePlayed(friend, out info)) continue;
                if (info.m_gameID.AppID() != appId || !info.m_steamIDLobby.IsValid() || info.m_steamIDLobby == lobby) continue;
                Plugin.Log.LogInfo("Found " + SteamFriends.GetFriendPersonaName(friend) + " in lobby " + info.m_steamIDLobby.m_SteamID + ", joining.");
                Join(info.m_steamIDLobby);
                return true;
            }
            status = "no friend is hosting";
            Plugin.Log.LogInfo("No friend is currently in a Below Zero lobby. Make sure they pressed " + Plugin.HostKey.Value + ".");
            return false;
        }

        /// <summary>Overlay-free join: the host's lobby id (copied to their clipboard on host) pasted into ours.</summary>
        public void JoinFromClipboard()
        {
            if (!initialized) return;
            ulong id;
            string text = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (ulong.TryParse(text, out id) && new CSteamID(id).IsLobby())
            {
                Plugin.Log.LogInfo("Joining lobby from clipboard: " + id);
                Join(new CSteamID(id));
            }
            else
            {
                status = "clipboard has no lobby id";
                Plugin.Log.LogInfo("Clipboard does not contain a lobby id (got '" + text + "').");
            }
        }

        public void Join(CSteamID lobbyId)
        {
            if (!initialized) return;
            if (IsInSession) Leave(false);
            if (LocalPlayerSync.InWorld)
            {
                // Joining from inside a world: go to the main menu first, the host's save is loaded from there.
                Plugin.Log.LogInfo("Leaving the current world before joining.");
                Plugin.Instance.StartCoroutine(QuitThenJoin(lobbyId));
                return;
            }
            status = "joining lobby...";
            SteamMatchmaking.JoinLobby(lobbyId);
        }

        /// <summary>Automatic joins (Steam launch argument, overlay, Discord) wait until the game is in a settled state.</summary>
        public void JoinWhenReady(CSteamID lobbyId)
        {
            Plugin.Instance.StartCoroutine(JoinWhenReadyRoutine(lobbyId));
        }

        private IEnumerator JoinWhenReadyRoutine(CSteamID lobbyId)
        {
            if (!LocalPlayerSync.InWorld) yield return MenuFlow.WaitForMenuReady();
            // An invite launched from Steam lands here the instant the menu appears, while the game's time scale
            // may still be 0 from the menu load and its cached scale unsettled. Joining right then loaded the world
            // with that 0 cached, and it came back the moment the last load freezer let go: a frozen arrival.
            // Pressing the join key seconds later never saw this. Give the menu the same few seconds.
            float deadline = Time.unscaledTime + 15f;
            while (Time.unscaledTime < deadline && (Time.timeScale < 0.99f || UWE.FreezeTime.HasFreezers())) yield return null;
            yield return new WaitForSecondsRealtime(1.5f);
            Join(lobbyId);
        }

        private IEnumerator QuitThenJoin(CSteamID lobbyId)
        {
            yield return MenuFlow.QuitToMainMenu();
            yield return MenuFlow.WaitForMenuReady();
            if (uGUI_MainMenu.main == null) { Plugin.Log.LogError("Main menu did not come back; press " + Plugin.JoinFriendKey.Value + " again from the menu."); yield break; }
            status = "joining lobby...";
            SteamMatchmaking.JoinLobby(lobbyId);
        }

        /// <summary>Leave the session. A client that is inside the host's world is sent back to the main menu.</summary>
        public void Leave() { Leave(true); }

        public void Leave(bool returnToMenu)
        {
            if (!initialized || !IsInSession) return;
            // Save inventory state before tearing down
            try { if (IsHost) InventorySync.HostSaveAllOnLeave(); else InventorySync.ClientSendInventoryToHost(); }
            catch (Exception e) { Plugin.Log.LogWarning("Inventory save on leave failed: " + e.Message); }
            InventorySync.Reset();
            bool clientInWorld = !IsHost && LocalPlayerSync.InWorld;
            foreach (var kv in identities)
            {
                var id = kv.Value;
                SteamNetworkingMessages.CloseSessionWithUser(ref id);
            }
            identities.Clear();
            reliableQueue.Clear();
            Saves.Reset();
            SteamMatchmaking.LeaveLobby(lobby);
            lobby = CSteamID.Nil;
            hostId = CSteamID.Nil;
            players.Clear();
            status = "idle";
            Plugin.Log.LogInfo("Left session.");
            if (returnToMenu && clientInWorld && !MenuFlow.Quitting)
            {
                Plugin.Log.LogInfo("Returning to the main menu.");
                Plugin.Instance.StartCoroutine(MenuFlow.QuitToMainMenu());
            }
        }

        /// <summary>Steam launches the game with "+connect_lobby ID" when a friend accepts an invite while the game is closed.</summary>
        public void CheckCommandLineJoin()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase))
                {
                    ulong id;
                    if (ulong.TryParse(args[i + 1], out id))
                    {
                        Plugin.Log.LogInfo("Command-line lobby join: " + id + " (waiting for the main menu).");
                        JoinWhenReady(new CSteamID(id));
                    }
                }
            }
        }

        private void OnLobbyCreated(LobbyCreated_t ev, bool ioFailure)
        {
            if (ioFailure || ev.m_eResult != EResult.k_EResultOK)
            {
                status = "lobby create failed: " + ev.m_eResult;
                Plugin.Log.LogError(status);
                return;
            }
            lobby = new CSteamID(ev.m_ulSteamIDLobby);
            hostId = selfId;
            SteamMatchmaking.SetLobbyData(lobby, LobbyKeyMarker, "1");
            SteamMatchmaking.SetLobbyData(lobby, LobbyKeyVersion, Plugin.PluginVersion);
            SteamMatchmaking.SetLobbyJoinable(lobby, true);
            status = "hosting";
            Plugin.Log.LogInfo("Lobby created " + lobby.m_SteamID + ".");
            OpenInvite();
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t ev)
        {
            Plugin.Log.LogInfo("Join requested via Steam overlay from " + ev.m_steamIDFriend.m_SteamID);
            JoinWhenReady(ev.m_steamIDLobby);
        }

        private void OnLobbyEnter(LobbyEnter_t ev)
        {
            var id = new CSteamID(ev.m_ulSteamIDLobby);
            if (SteamMatchmaking.GetLobbyOwner(id) == selfId)
            {
                // Echo of our own CreateLobby; OnLobbyCreated does the setup (order of the two is not guaranteed).
                lobby = id;
                hostId = selfId;
                return;
            }

            if (ev.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                status = "join failed: " + ev.m_EChatRoomEnterResponse;
                Plugin.Log.LogError(status);
                return;
            }
            lobby = id;
            hostId = SteamMatchmaking.GetLobbyOwner(lobby);
            string ver = SteamMatchmaking.GetLobbyData(lobby, LobbyKeyVersion);
            if (ver != Plugin.PluginVersion)
            {
                // Different builds desync in ways that look like gameplay bugs and cost hours to chase. Refuse the join.
                string hostVer = string.IsNullOrEmpty(ver) ? "an older version" : "version " + ver;
                Plugin.Log.LogWarning("Version mismatch: host runs BZMultiplayer " + hostVer
                                    + ", you run " + Plugin.PluginVersion + ". Leaving the session.");
                status = "version mismatch";
                if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(RefuseMismatch(hostVer));
                else Leave(true);
                return;
            }
            status = "connected";
            Plugin.Log.LogInfo("Joined lobby " + lobby.m_SteamID + ", host " + hostId.m_SteamID);
            SendHello(hostId, SendReliable);
        }

        /// <summary>
        /// Show the mismatch on screen for three seconds, then leave and go back to the main menu. ErrorMessage entries
        /// fade on their own, so the line is re-posted each second to keep it readable for the whole three.
        /// </summary>
        private System.Collections.IEnumerator RefuseMismatch(string hostVer)
        {
            string line = "BZMultiplayer version mismatch\nHost runs " + hostVer + ", you run version " + Plugin.PluginVersion
                        + ".\nBoth players need the same build. Returning to the main menu.";
            for (int i = 0; i < 3; i++)
            {
                try { ErrorMessage.AddMessage(line); } catch { }
                yield return new UnityEngine.WaitForSecondsRealtime(1f);
            }
            Leave(true);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t ev)
        {
            if (!IsInSession || ev.m_ulSteamIDLobby != lobby.m_SteamID) return;
            uint change = ev.m_rgfChatMemberStateChange;
            bool left = (change & (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                                         | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                                         | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                                         | EChatMemberStateChange.k_EChatMemberStateChangeBanned)) != 0;
            if (!left) return;
            ulong who = ev.m_ulSteamIDUserChanged;
            if (who == hostId.m_SteamID && !IsHost)
            {
                Plugin.Log.LogInfo("Host left. Leaving session.");
                Leave();
                return;
            }
            SteamNetworkingIdentity ident;
            if (identities.TryGetValue(who, out ident))
            {
                SteamNetworkingMessages.CloseSessionWithUser(ref ident);
                identities.Remove(who);
            }
            players.Remove(who);
            Plugin.Log.LogInfo("Player left: " + who);
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t ev)
        {
            var remote = ev.m_identityRemote.GetSteamID();
            if (!IsInSession || !IsLobbyMember(remote))
            {
                if (IsInSession) Plugin.Log.LogWarning("Rejected session from non-member " + remote.m_SteamID);
                return;
            }
            var ident = ev.m_identityRemote;
            SteamNetworkingMessages.AcceptSessionWithUser(ref ident);
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t ev)
        {
            ulong who = ev.m_info.m_identityRemote.GetSteamID64();
            if (!IsInSession) { identities.Remove(who); return; }
            Plugin.Log.LogWarning("Session failed with " + who + ": " + ev.m_info.m_eEndReason + " - resetting session so the next packet reconnects.");
            var ident = ev.m_info.m_identityRemote;
            SteamNetworkingMessages.CloseSessionWithUser(ref ident);
            identities.Remove(who);
            if (IsInSession && !IsHost && who == hostId.m_SteamID)
            {
                // Re-introduce ourselves; the host will re-send Hello and (if needed) the world.
                SendHello(hostId, SendReliable);
            }
        }

        private bool IsLobbyMember(CSteamID id)
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == id) return true;
            return false;
        }

        // ---------------------------------------------------------------- sending

        private SteamNetworkingIdentity IdentityFor(ulong steamId)
        {
            SteamNetworkingIdentity ident;
            if (!identities.TryGetValue(steamId, out ident))
            {
                ident = new SteamNetworkingIdentity();
                ident.SetSteamID64(steamId);
                identities[steamId] = ident;
            }
            return ident;
        }

        private void SendTo(ulong steamId, PacketWriter pw, int flags)
        {
            if (steamId == selfId.m_SteamID) return;
            if (flags == SendReliable)
            {
                // Reliable traffic goes through a queue so bulk transfers never overflow Steam's send buffer.
                var copy = new byte[pw.Length];
                Buffer.BlockCopy(pw.Buffer, 0, copy, 0, pw.Length);
                reliableQueue.Enqueue(new Queued { To = steamId, Data = copy });
                return;
            }
            SendRaw(steamId, pw.Buffer, pw.Length, flags);
        }

        private EResult SendRaw(ulong steamId, byte[] data, int len, int flags)
        {
            if (len > sendPinned.Length)
            if (len > sendPinned.Length)
            {
                sendPin.Free();
                sendPinned = new byte[Mathf.NextPowerOfTwo(len)];
                sendPin = GCHandle.Alloc(sendPinned, GCHandleType.Pinned);
            }
            Buffer.BlockCopy(data, 0, sendPinned, 0, len);
            var ident = IdentityFor(steamId);
            var res = SteamNetworkingMessages.SendMessageToUser(ref ident, sendPin.AddrOfPinnedObject(), (uint)len, flags, Channel);
            if (res != EResult.k_EResultOK && res != EResult.k_EResultNoConnection && res != EResult.k_EResultLimitExceeded && Plugin.VerboseLog.Value)
                Plugin.Log.LogWarning("Send to " + steamId + " -> " + res);
            return res;
        }

        /// <summary>Drains the reliable queue; stops for this frame when Steam's send buffer is full.</summary>
        private void FlushReliable()
        {
            int budget = 64;
            while (reliableQueue.Count > 0 && budget-- > 0)
            {
                var q = reliableQueue.Peek();
                var res = SendRaw(q.To, q.Data, q.Data.Length, SendReliable);
                if (res == EResult.k_EResultLimitExceeded) return;      // buffer full, retry next frame
                reliableQueue.Dequeue();
                if (res != EResult.k_EResultOK && res != EResult.k_EResultNoConnection)
                    Plugin.Log.LogWarning("Reliable send to " + q.To + " failed: " + res);
            }
        }

        private void SendToAllExcept(ulong except, PacketWriter pw, int flags)
        {
            foreach (var p in players.All)
                if (p.SteamId != except) SendTo(p.SteamId, pw, flags);
        }

        private void SendHello(CSteamID to, int flags)
        {
            writer.Begin(PacketType.Hello);
            writer.Write(selfId.m_SteamID);
            writer.Write(SelfName);
            writer.Write(VR.VRBridge.IsVRActive);
            SendTo(to.m_SteamID, writer, flags);
        }

        private void SendHelloAbout(RemotePlayer about, ulong to)
        {
            writer.Begin(PacketType.Hello);
            writer.Write(about.SteamId);
            writer.Write(about.Name);
            writer.Write(about.IsVR);
            SendTo(to, writer, SendReliable);
        }

        /// <summary>Called by LocalPlayerSync at the configured rate.</summary>
        public void SendPose(ref PlayerPose pose)
        {
            if (!IsInSession) return;
            writer.Begin(PacketType.PlayerPose);
            writer.Write(ref pose);
            if (IsHost) SendToAllExcept(selfId.m_SteamID, writer, SendUnreliable);
            else SendTo(hostId.m_SteamID, writer, SendUnreliable);
        }

        // ---------------------------------------------------------------- save transfer packets

        public void SendSaveRequest()
        {
            if (!IsInSession || IsHost) return;
            writer.Begin(PacketType.SaveRequest);
            SendTo(hostId.m_SteamID, writer, SendReliable);
        }

        public void SendSaveStatus(ulong to, string text)
        {
            writer.Begin(PacketType.SaveStatus);
            writer.Write(text);
            SendTo(to, writer, SendReliable);
        }

        public void SendSaveBegin(ulong to, string slot, int fileCount, long totalBytes)
        {
            writer.Begin(PacketType.SaveBegin);
            writer.Write(slot); writer.Write(fileCount); writer.Write(totalBytes);
            SendTo(to, writer, SendReliable);
        }

        public void SendSaveFile(ulong to, int index, string rel, int size)
        {
            writer.Begin(PacketType.SaveFile);
            writer.Write(index); writer.Write(rel); writer.Write(size);
            SendTo(to, writer, SendReliable);
        }

        public void SendSaveChunk(ulong to, int index, int offset, byte[] data, int len)
        {
            writer.Begin(PacketType.SaveChunk);
            writer.Write(index); writer.Write(offset); writer.Write(data, offset, len);
            SendTo(to, writer, SendReliable);
        }

        public void SendSaveAbort(ulong to, string reason)
        {
            writer.Begin(PacketType.SaveAbort);
            writer.Write(reason);
            SendTo(to, writer, SendReliable);
        }

        public void SendSaveEnd(ulong to)
        {
            writer.Begin(PacketType.SaveEnd);
            SendTo(to, writer, SendReliable);
        }

        // ---------------------------------------------------------------- world events

        /// <summary>Client -> host; host -> everyone else. Reliable.</summary>
        private void SendWorldEvent(PacketWriter pw)
        {
            if (!IsInSession) return;
            if (IsHost) SendToAllExcept(selfId.m_SteamID, pw, SendReliable);
            else SendTo(hostId.m_SteamID, pw, SendReliable);
        }

        public void SendClock(double timePassed)
        {
            if (!IsHost) return;
            writer.Begin(PacketType.Clock); writer.Write(timePassed);
            SendToAllExcept(selfId.m_SteamID, writer, SendUnreliable);
        }
        public void SendItemPickup(string id) { writer.Begin(PacketType.ItemPickup); writer.Write(id); SendWorldEvent(writer); }
        public void SendItemDrop(int tt, string id, Vector3 pos, Quaternion rot) { writer.Begin(PacketType.ItemDrop); writer.Write(tt); writer.Write(id); writer.Write(pos); writer.Write(rot); SendWorldEvent(writer); }
        public void SendPlaced(int tt, string id, Vector3 pos, Quaternion rot, string parentId) { writer.Begin(PacketType.Placed); writer.Write(tt); writer.Write(id); writer.Write(pos); writer.Write(rot); writer.Write(parentId); SendWorldEvent(writer); }
        public void SendContainerAdd(string cid, string iid, int tt) { writer.Begin(PacketType.ContainerAdd); writer.Write(cid); writer.Write(iid); writer.Write(tt); SendWorldEvent(writer); }
        public void SendContainerRemove(string cid, string iid) { writer.Begin(PacketType.ContainerRemove); writer.Write(cid); writer.Write(iid); SendWorldEvent(writer); }
        public void SendBaseState(string baseId, bool isNew, Vector3 pos, Quaternion rot, byte[] data)
        {
            writer.Begin(PacketType.BaseState); writer.Write(baseId); writer.Write(isNew); writer.Write(pos); writer.Write(rot); writer.Write(data, 0, data.Length); SendWorldEvent(writer);
        }
        public void SendBaseRemoved(string baseId) { writer.Begin(PacketType.BaseRemoved); writer.Write(baseId); SendWorldEvent(writer); }
        public void SendHeldItem(int techType, int flags) { writer.Begin(PacketType.HeldItem); writer.Write(selfId.m_SteamID); writer.Write(techType); writer.Write(flags); SendWorldEvent(writer); }
        public void SendStory(byte kind, string key, int techType) { writer.Begin(PacketType.Story); writer.Write(kind); writer.Write(key); writer.Write(techType); SendWorldEvent(writer); }

        /// <summary>Host: apply the configured player limit to the current lobby.</summary>
        /// <summary>How many players this lobby accepts, as Steam itself reports it (0 when not in a session).</summary>
        public int LobbyLimit
        {
            get
            {
                if (!IsInSession) return 0;
                int limit = SteamMatchmaking.GetLobbyMemberLimit(lobby);
                return limit > 0 ? limit : Mathf.Clamp(Plugin.MaxPlayers.Value, 2, 8);
            }
        }

        public void ApplyMaxPlayers()
        {
            if (!IsInSession || !IsHost) return;
            SteamMatchmaking.SetLobbyMemberLimit(lobby, Mathf.Clamp(Plugin.MaxPlayers.Value, 2, 8));
        }

        public void SendResourceBroken(string id) { writer.Begin(PacketType.ResourceBroken); writer.Write(id); SendWorldEvent(writer); }
        public void SendProgress(string id, float amount) { writer.Begin(PacketType.Progress); writer.Write(id); writer.Write(amount); SendWorldEvent(writer); }

        /// <summary>Send a pre-built inventory packet to a specific player (reliable).</summary>
        public void SendInventoryPacket(ulong to, PacketWriter pw) { reliableQueue.Enqueue(new Queued { To = to, Data = CopyPacket(pw) }); }

        /// <summary>Host: ask all connected clients to send their current inventory.</summary>
        public void BroadcastInventoryRequest(PacketWriter pw)
        {
            byte[] data = CopyPacket(pw);
            foreach (var p in players.All)
                reliableQueue.Enqueue(new Queued { To = p.SteamId, Data = (byte[])data.Clone() });
        }

        private static byte[] CopyPacket(PacketWriter pw) { var b = new byte[pw.Length]; Buffer.BlockCopy(pw.Buffer, 0, b, 0, pw.Length); return b; }

        /// <summary>Host re-broadcasts a client's world event to the other clients, byte for byte.</summary>
        private void RelayIfHost(ulong from, byte[] data, int size)
        {
            if (!IsHost || from == selfId.m_SteamID) return;
            foreach (var p in players.All)
                if (p.SteamId != from) { var copy = new byte[size]; Buffer.BlockCopy(data, 0, copy, 0, size); reliableQueue.Enqueue(new Queued { To = p.SteamId, Data = copy }); }
        }

        // ---------------------------------------------------------------- receiving

        // How long a player may go without sending anything before they are treated as gone. Pose packets arrive
        // many times a second and the held item repeats every five, so twelve seconds of nothing is a dead process
        // or a dead link, not a quiet player. Steam's own lobby timeout for an abrupt exit can take a minute.
        private const float SilenceTimeout = 12f;
        private float nextSilenceCheck;

        private void DropSilentPlayers()
        {
            if (Time.unscaledTime < nextSilenceCheck) return;
            nextSilenceCheck = Time.unscaledTime + 1f;
            List<ulong> gone = null;
            foreach (var p in players.All)
            {
                int age = p.AgeMs;
                if (age < 0 || age < SilenceTimeout * 1000f) continue;
                if (gone == null) gone = new List<ulong>();
                gone.Add(p.SteamId);
            }
            if (gone == null) return;
            foreach (var who in gone)
            {
                if (who == hostId.m_SteamID && !IsHost)
                {
                    Plugin.Log.LogInfo("Host has been silent for " + SilenceTimeout + "s; leaving the session.");
                    Leave();
                    return;
                }
                SteamNetworkingIdentity ident;
                if (identities.TryGetValue(who, out ident))
                {
                    SteamNetworkingMessages.CloseSessionWithUser(ref ident);
                    identities.Remove(who);
                }
                players.Remove(who);
                Plugin.Log.LogInfo("Player timed out (silent for " + SilenceTimeout + "s): " + who);
            }
        }

        public void Pump()
        {
            if (!initialized || !IsInSession) return;
            FlushReliable();
            DropSilentPlayers();
            int n;
            while ((n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, recvPtrs, recvPtrs.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    var msg = SteamNetworkingMessage_t.FromIntPtr(recvPtrs[i]);
                    try
                    {
                        int size = msg.m_cbSize;
                        if (size > recvBuf.Length) recvBuf = new byte[Mathf.NextPowerOfTwo(size)];
                        Marshal.Copy(msg.m_pData, recvBuf, 0, size);
                        Handle(msg.m_identityPeer.GetSteamID64(), recvBuf, size);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("Packet handling error: " + e);
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(recvPtrs[i]);
                    }
                }
            }
        }

        private void Handle(ulong from, byte[] data, int size)
        {
            if (size < 1) return;
            var r = new PacketReader(data, size);
            var type = r.ReadType();
            if (Plugin.VerboseLog.Value) Plugin.Log.LogDebug("recv " + type + " from " + from + " (" + size + " bytes)");

            switch (type)
            {
                case PacketType.Hello:
                {
                    ulong id = r.ReadULong();
                    string name = r.ReadString();
                    bool isVr = r.ReadBool();
                    bool isNew = players.Get(id) == null;
                    var rp = players.GetOrAdd(id, name, isVr);
                    Plugin.Log.LogInfo("Hello from " + name + " (" + id + ")" + (isVr ? " [VR]" : ""));
                    if (!IsHost && from == hostId.m_SteamID && id == hostId.m_SteamID && Plugin.AutoSyncSave.Value)
                    {
                        Plugin.Log.LogInfo("Requesting the host's world.");
                        Saves.OnSaveStatus("waiting for host world...");
                        SendSaveRequest();
                    }
                    if (IsHost && from == id)
                    {
                        // Introduce ourselves and everyone else to the newcomer, and the newcomer to everyone else.
                        SendHello(new CSteamID(id), SendReliable);
                        foreach (var other in players.All)
                        {
                            if (other.SteamId == id) continue;
                            SendHelloAbout(other, id);
                            if (isNew) SendHelloAbout(rp, other.SteamId);
                        }
                    }
                    break;
                }
                case PacketType.PlayerPose:
                {
                    var pose = r.ReadPose();
                    if (pose.SteamId == selfId.m_SteamID) break;
                    var rp = players.Get(pose.SteamId);
                    if (rp == null)
                    {
                        // Pose before Hello (packet reorder) - create with a placeholder name.
                        rp = players.GetOrAdd(pose.SteamId, SteamFriends.GetFriendPersonaName(new CSteamID(pose.SteamId)), pose.IsVR);
                    }
                    rp.PushPose(ref pose);
                    if (IsHost && from == pose.SteamId)
                    {
                        writer.Begin(PacketType.PlayerPose);
                        writer.Write(ref pose);
                        SendToAllExcept(pose.SteamId, writer, SendUnreliable);
                    }
                    break;
                }
                case PacketType.PlayerLeft:
                {
                    players.Remove(r.ReadULong());
                    break;
                }
                case PacketType.SaveRequest:
                {
                    if (IsHost) Saves.OnSaveRequested(from);
                    break;
                }
                case PacketType.SaveStatus:
                {
                    if (from == hostId.m_SteamID) Saves.OnSaveStatus(r.ReadString());
                    break;
                }
                case PacketType.SaveBegin:
                {
                    if (from != hostId.m_SteamID) break;
                    string slot = r.ReadString(); int count = r.ReadInt(); long total = r.ReadLong();
                    Saves.OnSaveBegin(slot, count, total);
                    break;
                }
                case PacketType.SaveFile:
                {
                    if (from != hostId.m_SteamID) break;
                    int index = r.ReadInt(); string rel = r.ReadString(); int fsize = r.ReadInt();
                    Saves.OnSaveFile(index, rel, fsize);
                    break;
                }
                case PacketType.SaveChunk:
                {
                    if (from != hostId.m_SteamID) break;
                    int index = r.ReadInt(); int offset = r.ReadInt();
                    int len; int start = r.ReadBlob(out len);
                    Saves.OnSaveChunk(index, offset, data, start, len);
                    break;
                }
                case PacketType.SaveEnd:
                {
                    if (from == hostId.m_SteamID) Saves.OnSaveEnd();
                    break;
                }
                case PacketType.SaveAbort:
                {
                    if (from == hostId.m_SteamID) Saves.OnSaveAbort(r.ReadString());
                    break;
                }
                case PacketType.Clock:
                {
                    if (from == hostId.m_SteamID) WorldSync.OnClock(r.ReadDouble());
                    break;
                }
                case PacketType.ItemPickup:
                {
                    RelayIfHost(from, data, size);
                    WorldSync.OnItemPickup(r.ReadString());
                    break;
                }
                case PacketType.BaseState:
                {
                    RelayIfHost(from, data, size);
                    string baseId = r.ReadString(); bool isNew = r.ReadBool(); Vector3 pos = r.ReadVector3(); Quaternion rot = r.ReadQuaternion();
                    int len; int off = r.ReadBlob(out len);
                    var blob = new byte[len]; Array.Copy(data, off, blob, 0, len);
                    BaseSync.OnBaseState(baseId, isNew, pos, rot, blob);
                    break;
                }
                case PacketType.BaseRemoved:
                {
                    RelayIfHost(from, data, size);
                    BaseSync.OnBaseRemoved(r.ReadString());
                    break;
                }
                case PacketType.HeldItem:
                {
                    RelayIfHost(from, data, size);
                    ulong owner = r.ReadULong(); int tt = r.ReadInt(); int flags = r.ReadInt();
                    players.SetHeldItem(owner, tt, flags);
                    break;
                }
                case PacketType.Story:
                {
                    RelayIfHost(from, data, size);
                    byte kind = r.ReadByte(); string key = r.ReadString(); int tt = r.ReadInt();
                    StorySync.OnStory(kind, key, tt);
                    break;
                }
                case PacketType.ResourceBroken:
                {
                    RelayIfHost(from, data, size);
                    WorldSync.OnResourceBroken(r.ReadString());
                    break;
                }
                case PacketType.ItemDrop:
                {
                    RelayIfHost(from, data, size);
                    int tt = r.ReadInt(); string id = r.ReadString(); Vector3 pos = r.ReadVector3(); Quaternion rot = r.ReadQuaternion();
                    WorldSync.OnItemDrop(tt, id, pos, rot);
                    break;
                }
                case PacketType.Placed:
                {
                    RelayIfHost(from, data, size);
                    int tt = r.ReadInt(); string id = r.ReadString(); Vector3 pos = r.ReadVector3(); Quaternion rot = r.ReadQuaternion(); string parent = r.ReadString();
                    WorldSync.OnPlaced(tt, id, pos, rot, parent);
                    break;
                }
                case PacketType.ContainerAdd:
                {
                    RelayIfHost(from, data, size);
                    string cid = r.ReadString(); string iid = r.ReadString(); int tt = r.ReadInt();
                    Plugin.Log.LogInfo("Packet ContainerAdd from " + from + ": " + (TechType)tt + " [" + iid + "] -> [" + cid + "]");
                    WorldSync.OnContainerAdd(cid, iid, tt);
                    break;
                }
                case PacketType.ContainerRemove:
                {
                    RelayIfHost(from, data, size);
                    string cid = r.ReadString(); string iid = r.ReadString();
                    Plugin.Log.LogInfo("Packet ContainerRemove from " + from + ": [" + iid + "] <- [" + cid + "]");
                    WorldSync.OnContainerRemove(cid, iid);
                    break;
                }
                case PacketType.Progress:
                {
                    RelayIfHost(from, data, size);
                    string id = r.ReadString(); float amount = r.ReadFloat();
                    WorldSync.OnProgress(id, amount);
                    break;
                }
                case PacketType.InventoryData:
                {
                    if (IsHost) InventorySync.OnInventoryDataFromClient(from, r);
                    else InventorySync.OnInventoryDataFromHost(r);
                    break;
                }
                case PacketType.InventoryRequest:
                {
                    if (IsHost) InventorySync.HostCheckAndSendInventory(from);
                    else InventorySync.ClientSendInventoryToHost();
                    break;
                }
            }
        }

        public string StatusLine()
        {
            if (!IsInSession) return status;
            return (IsHost ? "hosting" : "connected") + "  lobby " + lobby.m_SteamID + "  players " + (players.Count + 1);
        }
    }
}
