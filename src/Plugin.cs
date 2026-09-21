using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using BZMultiplayer.Net;
using BZMultiplayer.Sync;

namespace BZMultiplayer
{
    /// <summary>
    /// Entry point. Owns the Steam transport, the local-player sampler and the remote-player registry,
    /// and drives them from Unity's Update loop.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.blowntobytes.bzmultiplayer";
        public const string PluginName = "BZMultiplayer";
        public const string PluginVersion = "0.8.1";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        // Config
        public static ConfigEntry<KeyCode> HostKey;
        public static ConfigEntry<KeyCode> InviteKey;
        public static ConfigEntry<KeyCode> LeaveKey;
        public static ConfigEntry<KeyCode> JoinFriendKey;
        public static ConfigEntry<KeyCode> JoinClipboardKey;
        public static ConfigEntry<KeyCode> RequestWorldKey;
        public static ConfigEntry<bool> AutoSyncSave;
        public static ConfigEntry<string> MultiplayerSlot;
        public static ConfigEntry<int> SendRate;
        public static ConfigEntry<bool> ShowOverlay;
        public static ConfigEntry<bool> VerboseLog;
        public static ConfigEntry<string> DiscordAppId;
        public static ConfigEntry<bool> DiscordEnabled;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<bool> DisableAchievements;
        public static ConfigEntry<bool> RemoteDatabankAudio;

        public SteamNet Net { get; private set; }
        public PlayerRegistry Players { get; private set; }
        public LocalPlayerSync Local { get; private set; }
        public DiscordRpc Discord { get; private set; }
        private long sessionStartUnix;
        private float nextPresenceAt;

        private bool steamReady;
        private float steamRetryAt;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            HostKey = Config.Bind("Keys", "HostSession", KeyCode.F11, "Create a Steam lobby and open the invite dialog. Press again while hosting to re-open the invite / re-copy the lobby id.");
            InviteKey = Config.Bind("Keys", "InviteAgain", KeyCode.None, "Optional separate key to re-open the invite dialog (Host key already does this while hosting).");
            LeaveKey = Config.Bind("Keys", "LeaveSession", KeyCode.End, "Leave the current session.");
            JoinFriendKey = Config.Bind("Keys", "JoinSession", KeyCode.F4, "Join whichever Steam friend is hosting a Below Zero lobby; if none is found, joins the lobby id in the clipboard.");
            JoinClipboardKey = Config.Bind("Keys", "JoinClipboardOnly", KeyCode.None, "Optional separate key to join only from a clipboard lobby id (Join key already falls back to this).");
            RequestWorldKey = Config.Bind("Keys", "ResendWorld", KeyCode.Home, "As a client: ask the host to send their world again (normally automatic on join).");
            SendRate = Config.Bind("Network", "SendRate", 20, "Player pose updates per second (5-60).");
            MaxPlayers = Config.Bind("Network", "MaxPlayers", 8, "Lobby size when hosting, including you (2-8).");
            DisableAchievements = Config.Bind("Gameplay", "DisableAchievements", false, "Do not unlock Steam achievements while the mod is loaded.");
            RemoteDatabankAudio = Config.Bind("Story", "RemoteDatabankAudio", false, "Play the databank narration (new creature/fragment entries) when another player makes the discovery. The entry and its notification always arrive either way; the main story lines always play for everyone.");
            AutoSyncSave = Config.Bind("World", "AutoSyncSave", true, "On join, download the host's save and load it automatically.");
            MultiplayerSlot = Config.Bind("World", "MultiplayerSlot", "slot9990", "Save slot the host's world is written into on this machine (slot0000-slot9999). It is overwritten on every join.");
            ShowOverlay = Config.Bind("UI", "ShowOverlay", true, "Draw the status box on the desktop window (not visible in the headset).");
            VerboseLog = Config.Bind("Debug", "Verbose", false, "Log every packet type received (spammy).");
            DiscordEnabled = Config.Bind("Discord", "Enabled", true, "Show the session in Discord (rich presence) with a Join button for friends. Needs the Discord desktop app running.");
            DiscordAppId = Config.Bind("Discord", "ApplicationId", "1550629787389792326", "Discord application id used for rich presence. Everyone in a session must use the same id. Create one at discord.com/developers/applications (New Application, name it e.g. 'Subnautica: Below Zero') and paste its Application ID here.");

            // The game's "Cleaner" scene (quit to main menu) destroys every root object that is not marked preserved,
            // including BepInEx's plugin object. Keep us (and every other plugin on this object) alive across it.
            try { if (GetComponent<SceneCleanerPreserve>() == null) gameObject.AddComponent<SceneCleanerPreserve>(); }
            catch (Exception e) { Log.LogWarning("Could not mark plugin object as preserved: " + e.Message); }

            Players = new PlayerRegistry();
            Net = new SteamNet(Players);
            Local = new LocalPlayerSync(Net);
            if (DiscordEnabled.Value && string.IsNullOrEmpty(DiscordAppId.Value.Trim())) Log.LogInfo("Discord presence disabled: no ApplicationId set in the config.");
            if (DiscordEnabled.Value && !string.IsNullOrEmpty(DiscordAppId.Value.Trim()))
            {
                Discord = new DiscordRpc(DiscordAppId.Value.Trim());
                Discord.OnJoinSecret += secret => { if (!Net.JoinById(secret)) Log.LogWarning("Discord join secret is not a Steam lobby id: " + secret); };
            }

            try
            {
                var harmony = new Harmony(PluginGuid);
                BodyTemplate.Install(harmony);
                WorldSync.Install(harmony, Net);
                BaseSync.Install(harmony, Net);
                StorySync.Install(harmony, Net);
                BZMultiplayer.UI.OptionsTab.Install(harmony);
                BZMultiplayer.UI.MainMenuEntry.Install(harmony);
                HeldItemSync.Install(Net);
            }
            catch (Exception e) { Log.LogError("Harmony patch failed: " + e); }

            Log.LogInfo(PluginName + " " + PluginVersion + " loaded. Host: " + HostKey.Value + "  Leave: " + LeaveKey.Value + "  Join: " + JoinFriendKey.Value + "  RequestWorld: " + RequestWorldKey.Value);
        }

        private void Update()
        {
            if (!steamReady)
            {
                if (Time.unscaledTime < steamRetryAt) return;
                steamRetryAt = Time.unscaledTime + 1f;
                steamReady = Net.TryInit();
                if (!steamReady) return;
                Net.CheckCommandLineJoin();
            }

            Net.Pump();
            MenuFlow.Tick();

            if (Input.GetKeyDown(HostKey.Value)) { if (Net.IsHost) Net.OpenInvite(); else Net.Host(); }
            if (Input.GetKeyDown(InviteKey.Value)) Net.OpenInvite();
            if (Input.GetKeyDown(LeaveKey.Value)) Net.Leave();
            if (Input.GetKeyDown(JoinFriendKey.Value)) { if (!Net.JoinFriend()) Net.JoinFromClipboard(); }
            if (Input.GetKeyDown(JoinClipboardKey.Value)) Net.JoinFromClipboard();
            if (Input.GetKeyDown(RequestWorldKey.Value)) Net.SendSaveRequest();

            Local.Update();
            Players.Update();
            Net.Saves.Update();
            WorldSync.Update();
            BaseSync.Update();
            HeldItemSync.Update();
            UpdateDiscord();
        }

        /// <summary>Start or stop the Discord client when the setting changes from the Options tab.</summary>
        public void ApplyDiscordSetting()
        {
            bool want = DiscordEnabled.Value && !string.IsNullOrEmpty(DiscordAppId.Value.Trim());
            if (want && Discord == null)
            {
                Discord = new DiscordRpc(DiscordAppId.Value.Trim());
                Discord.OnJoinSecret += secret => { if (!Net.JoinById(secret)) Log.LogWarning("Discord join secret is not a Steam lobby id: " + secret); };
            }
            else if (!want && Discord != null) { Discord.Dispose(); Discord = null; }
        }

        private void UpdateDiscord()
        {
            if (Discord == null) return;
            Discord.Update(Time.unscaledTime);
            if (!Discord.Connected || Time.unscaledTime < nextPresenceAt) return;
            nextPresenceAt = Time.unscaledTime + 2f;
            if (Net.IsInSession)
            {
                if (sessionStartUnix == 0) sessionStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int n = Net.LobbyMemberCount;
                string details = Net.IsHost ? "Hosting a world" : "In " + Net.HostName + "'s world";
                Discord.SetActivity(details, (LocalPlayerSync.InWorld ? "Exploring" : "Loading") + (BZMultiplayer.VR.VRBridge.IsVRActive ? " in VR" : ""), Net.LobbyId.ToString(), n, 8, sessionStartUnix);
            }
            else
            {
                sessionStartUnix = 0;
                Discord.SetActivity("Playing solo", LocalPlayerSync.InWorld ? "In the world" : "In the menu", null, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
        }

        private void OnGUI()
        {
            if (!ShowOverlay.Value || !steamReady) return;
            string saveLine = Net.Saves.StatusText;
            int extra = string.IsNullOrEmpty(saveLine) ? 0 : 1;
            GUI.Box(new Rect(10, 10, 360, 24 + 18 * (2 + extra + Players.Count)), "");
            GUI.Label(new Rect(16, 12, 350, 20), "BZMultiplayer " + PluginVersion + "  -  " + Net.StatusLine());
            if (extra == 1) GUI.Label(new Rect(16, 30 + 18 * (1 + Players.Count), 350, 20), "  world: " + saveLine);
            if (Discord != null) GUI.Label(new Rect(250, 12, 120, 20), "discord: " + Discord.Status);
            GUI.Label(new Rect(16, 30, 350, 20), Net.IsInSession
                ? LeaveKey.Value + " leave" + (Net.IsHost ? "   " + HostKey.Value + " invite again (id -> clipboard)" : "   " + RequestWorldKey.Value + " re-download world")
                : HostKey.Value + " host   " + JoinFriendKey.Value + " join friend (or clipboard lobby id)");
            int y = 48;
            foreach (var p in Players.All)
            {
                GUI.Label(new Rect(16, y, 350, 20), "  " + p.Name + (p.IsVR ? " [VR]" : "") + "   " + p.AgeMs + " ms");
                y += 18;
            }
        }

        private void OnDestroy()
        {
            Log.LogInfo("Plugin object destroyed (game shutting down?)");
            if (Discord != null) Discord.Dispose();
            Net.Leave(false);
        }
    }
}
