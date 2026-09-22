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
        public const string PluginVersion = "0.12.0";

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
        public static ConfigEntry<bool> ShowMainMenuEntry;
        public static ConfigEntry<bool> ShowOptionsTab;
        public static ConfigEntry<bool> KeepWorldRunning;
        public static ConfigEntry<bool> VerboseLog;
        public static ConfigEntry<string> DiscordAppId;
        public static ConfigEntry<bool> DiscordEnabled;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<bool> DisableAchievements;
        public static ConfigEntry<bool> RemoteDatabankAudio;
        public static ConfigEntry<float> HeldItemScale;
        public static ConfigEntry<string> HeldItemOffsets;
        public static ConfigEntry<KeyCode> TuneHeldItemsKey;
        public static ConfigEntry<KeyCode> TuneModeKey;
        public static ConfigEntry<KeyCode> TuneSaveKey;
        public static ConfigEntry<KeyCode> TuneResetKey;

        public SteamNet Net { get; private set; }
        public PlayerRegistry Players { get; private set; }
        public LocalPlayerSync Local { get; private set; }
        public DiscordRpc Discord { get; private set; }
        private long sessionStartUnix;
        private long soloStartUnix;
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
            DisableAchievements = Config.Bind("Gameplay", "DisableAchievements", true, "Do not unlock Steam achievements while the mod is loaded.");
            RemoteDatabankAudio = Config.Bind("Story", "RemoteDatabankAudio", false, "Play the databank narration (new creature/fragment entries) when another player makes the discovery. The entry and its notification always arrive either way; the main story lines always play for everyone.");
            HeldItemScale = Config.Bind("Avatar", "HeldItemScale", 1.0f, "Overall size of the item in another player's hand. 1.0 keeps the item at its normal world size; raise or lower it if every item looks uniformly too big or too small.");
            HeldItemOffsets = Config.Bind("Avatar", "HeldItemOffsets", "Knife:0.051,-0.03,-0.13,20,-100,-125,1;Flashlight:0.04,0,-0.15,95,15,0,1;Scanner:0.08,-0.05,-0.2,-160,-100,-55,1;Seaglide:-0.16,0,0,70,65,25,0.6;Builder:0.04,-0.02,-0.11,-345,-90,-95,1;MetalDetector:0.0514,-0.0012,-0.1606,106.693,-343.8589,11.271,1;PropulsionCannon:0.0134,-0.2637,-0.0849,51.7259,-7.6738,-10.5686,1;AirBladder:0.046,-0.012,-0.1648,28.6593,0.9964,-66.6856,1;Welder:0.0459,-0.137,-0.1333,94.3373,-4.7145,-7.6686,0.9133;LaserCutter:0.0499,-0.116,-0.1394,97.3532,-5.0177,-11.9823,0.8834;DiveReel:0.0113,-0.1847,-0.2334,116.4855,-4.6702,-5.653,1;TeleportationTool:-0.1354,-0.1551,0.012,89.6348,-10.0281,-53.9805,1;Flare:0.0447,-0.0914,-0.1106,-242.3369,-10.0097,-10.0045,1;Coffee:0.0453,-0.0622,-0.1634,0,-97.3449,-109.4196,1;Thumper:-0.3536,-0.1048,-0.3088,78.1842,33.0364,-1.6332,1", "Where each item sits in another player's hand. These are eyeballed starting points from a flat-screen tuning pass, not final - expect to adjust them, and use the TuneHeldItems key to do it. Format: 'TechType:px,py,pz,rx,ry,rz,scale' separated by ';'. Position is in metres, rotation in degrees, scale is a multiplier. An item with no entry sits at the bare hand bone.");
            TuneHeldItemsKey = Config.Bind("Keys", "TuneHeldItems", KeyCode.F9, "Flat screen only: start or stop nudging the item in another player's hand until it looks right. Set to None to disable. Everything the tuner uses is on the numpad and the F keys, so it never fights the game's own controls.");
            TuneModeKey = Config.Bind("Keys", "TuneCycleMode", KeyCode.F7, "While tuning: switch between moving, rotating and scaling.");
            TuneSaveKey = Config.Bind("Keys", "TuneSave", KeyCode.F8, "While tuning: save this item's numbers into HeldItemOffsets.");
            TuneResetKey = Config.Bind("Keys", "TuneReset", KeyCode.F6, "While tuning: put this item back where it started.");
            AutoSyncSave = Config.Bind("World", "AutoSyncSave", true, "On join, download the host's save and load it automatically.");
            MultiplayerSlot = Config.Bind("World", "MultiplayerSlot", "slot9990", "Save slot the host's world is written into on this machine (slot0000-slot9999). It is overwritten on every join.");
            ShowOverlay = Config.Bind("UI", "ShowOverlay", true, "Draw the status box on the desktop window (not visible in the headset).");
            ShowOptionsTab = Config.Bind("UI", "ShowOptionsTab", true, "Add the Multiplayer tab to the game's Options screen. Turn this off to rule the tab out if other mods' settings misbehave.");
            KeepWorldRunning = Config.Bind("Gameplay", "KeepWorldRunning", true, "Do not let one player's PDA or pause menu freeze the world during a session.");
            ShowMainMenuEntry = Config.Bind("UI", "ShowMainMenuEntry", true, "Add a Multiplayer entry to the main menu, below Play. Turn this off to use only the Multiplayer tab in Options.");
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

            var harmony = new Harmony(PluginGuid);
            // Each install guarded on its own: one failed patch must never take the menu and everything after it down.
            Guard("BodyTemplate.Install(harmony)", () => { BodyTemplate.Install(harmony); });
            Guard("WorldSync.Install(harmony, Net)", () => { WorldSync.Install(harmony, Net); });
            Guard("BaseSync.Install(harmony, Net)", () => { BaseSync.Install(harmony, Net); });
            Guard("if (KeepWorldRunning.Value) TimeSync.Install(harmony, Net)", () => { if (KeepWorldRunning.Value) TimeSync.Install(harmony, Net); });
            Guard("SessionExit.Install(Net)", () => { SessionExit.Install(Net); });
            Guard("StorySync.Install(harmony, Net)", () => { StorySync.Install(harmony, Net); });
            Guard("if (ShowOptionsTab.Value) BZMultiplayer.UI.OptionsTab.Instal", () => { if (ShowOptionsTab.Value) BZMultiplayer.UI.OptionsTab.Install(harmony); });
            Guard("if (ShowMainMenuEntry.Value) BZMultiplayer.UI.MainMenuEntry.", () => { if (ShowMainMenuEntry.Value) BZMultiplayer.UI.MainMenuEntry.Install(harmony); });
            Guard("HeldItemSync.Install(Net)", () => { HeldItemSync.Install(Net); });
            Guard("CutsceneSync.Install(harmony, Net)", () => { CutsceneSync.Install(harmony, Net); });

            InventorySync.Init(Net);

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
            InventorySync.Update();
            SessionExit.Update();
            UI.OptionsTab.Update();
            UI.HeldItemTuner.Update();
            StuckProbe.Update();
            TimeSync.Update();
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
                Discord.SetActivity(details, (LocalPlayerSync.InWorld ? "Exploring" : "Loading") + (BZMultiplayer.VR.VRBridge.IsVRActive ? " in VR" : ""), Net.LobbyId.ToString(), n, Net.LobbyLimit, sessionStartUnix);
            }
            else
            {
                sessionStartUnix = 0;
                // A stable start time: a live clock here changed the payload every update, so the "nothing changed"
                // check never matched and presence was resent every couple of seconds forever.
                if (soloStartUnix == 0) soloStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Discord.SetActivity("Playing solo", LocalPlayerSync.InWorld ? "In the world" : "In the menu", null, 0, 0, soloStartUnix);
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

            UI.PlayerHUD.OnGUI();
        }

        /// <summary>Quitting to the desktop: close the lobby immediately rather than waiting for the process to die.</summary>
        private static void Guard(string what, Action install)
        {
            try { install(); }
            catch (Exception e) { Log.LogError("Install failed (" + what + "): " + e); }
        }

        private void OnApplicationQuit()
        {
            if (Net != null && Net.IsInSession) { Log.LogInfo("Quitting the game; closing the session."); Net.Leave(false); }
        }

        private void OnDestroy()
        {
            Log.LogInfo("Plugin object destroyed (game shutting down?)");
            if (Discord != null) Discord.Dispose();
            Net.Leave(false);
        }
    }
}
