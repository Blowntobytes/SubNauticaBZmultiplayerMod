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
        public const string PluginVersion = "0.0.1";
        /// <summary>Test build label (empty for a release). Test builds are not committed until they are confirmed working.</summary>
        public const string TestBuild = "";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        // Config
        public static KeyBind HostKey;
        public static KeyBind InviteKey;
        public static KeyBind LeaveKey;
        public static KeyBind JoinFriendKey;
        public static KeyBind JoinClipboardKey;
        public static KeyBind RequestWorldKey;
        public static ConfigEntry<bool> AutoSyncSave;
        public static ConfigEntry<string> MultiplayerSlot;
        public static ConfigEntry<int> SendRate;
        public static ConfigEntry<bool> ShowOverlay;
        public static ConfigEntry<bool> AutoSprint;
        public static ConfigEntry<bool> CutscenePullIn;
        public static ConfigEntry<bool> VRStickSprint;
        public static ConfigEntry<float> VRWalkSpeed;
        public static ConfigEntry<float> VRSprintSpeed;
        public static ConfigEntry<float> VRSwimSpeed;
        public static ConfigEntry<float> TestSpeed;
        public static ConfigEntry<bool> VerboseLog;
        public static ConfigEntry<string> DiscordAppId;
        public static ConfigEntry<bool> DiscordEnabled;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<bool> DisableAchievements;
        public static ConfigEntry<bool> RemoteDatabankAudio;
        public static ConfigEntry<float> HeldItemScale;
        public static ConfigEntry<string> HeldItemOffsets;
        public static ConfigEntry<string> FlatArmRaise;

        // Baked from BlownToBits' tuning pass on test18 (27 Sep 2026).
        public const string DefaultHeldItemOffsets = "Knife:0.051,-0.03,-0.13,20,-100,-125,1;Scanner:0.08,-0.05,-0.2,-160,-100,-55,1;PropulsionCannon:0.0134,-0.2637,-0.0849,51.7259,-7.6738,-10.5686,1;AirBladder:0.046,-0.012,-0.1648,28.6593,0.9964,-66.6856,1;Flare:0.0447,-0.0914,-0.1106,-242.3369,-10.0097,-10.0045,1;Coffee:0.0453,-0.0622,-0.1634,0,-97.3449,-109.4196,1;Seaglide:-0.152,0,-0.1789,60.5513,102.0439,108.0743,0.6;Builder:0.04,-0.02,-0.11,14.3925,-96.1917,-96.5722,1;Thumper:-0.0568,-0.0666,-0.4757,64.6707,91.2852,104.4695,0.7894;Gravsphere:-0.155,-0.0402,-0.1847,0,0,0,0.7079;Flashlight:0.04,0,-0.15,77.9104,85.7709,98.1516,1;Beacon:-0.0542,-0.0221,-0.2131,79.5097,103.6667,114.0483,1;DiveReel:0.0113,-0.1827,-0.2034,79.6441,174.8035,177.8674,1;TeleportationTool:-0.0972,-0.2135,-0.1987,67.273,85.9525,97.1859,1;MetalDetector:0.0494,0.0009,-0.1244,76.112,102.5291,112.8121,1;LaserCutter:0.0499,-0.116,-0.1394,77.9074,101.5898,103.1306,0.8834;SpyPenguin:-0.0983,-0.0321,-0.3893,84.5646,0,0,0.427;SpyPenguinRemote:0.0342,-0.0101,-0.1387,0,0,0,1;Welder:0.0621,-0.1169,-0.1293,77.9702,104.457,112.6501,0.9133;Boomerang:0.0642,-0.0603,-0.0223,76.0716,-22.9309,-40.7913,0.4775;Bladderfish:0.0901,0.0763,-0.1512,61.83,-90.0747,-143.6909,0.8894;Hoopfish:-0.01,-0.0763,-0.1451,23.3857,27.4525,-24.1926,0.6786;ArcticPeeper:0.0221,-0.0241,-0.1148,7.0256,0,0,0.3357";
        public const string DefaultFlatArmRaise = "30.1284,113.3181,26.0453";
        // Earlier shipped defaults: a config still holding one of these is upgraded to the new numbers.
        private const string OldHeldItemOffsets = "Knife:0.051,-0.03,-0.13,20,-100,-125,1;Flashlight:0.04,0,-0.15,95,15,0,1;Scanner:0.08,-0.05,-0.2,-160,-100,-55,1;Seaglide:-0.16,0,0,70,65,25,0.6;Builder:0.04,-0.02,-0.11,-345,-90,-95,1;MetalDetector:0.0514,-0.0012,-0.1606,106.693,-343.8589,11.271,1;PropulsionCannon:0.0134,-0.2637,-0.0849,51.7259,-7.6738,-10.5686,1;AirBladder:0.046,-0.012,-0.1648,28.6593,0.9964,-66.6856,1;Welder:0.0459,-0.137,-0.1333,94.3373,-4.7145,-7.6686,0.9133;LaserCutter:0.0499,-0.116,-0.1394,97.3532,-5.0177,-11.9823,0.8834;DiveReel:0.0113,-0.1847,-0.2334,116.4855,-4.6702,-5.653,1;TeleportationTool:-0.1354,-0.1551,0.012,89.6348,-10.0281,-53.9805,1;Flare:0.0447,-0.0914,-0.1106,-242.3369,-10.0097,-10.0045,1;Coffee:0.0453,-0.0622,-0.1634,0,-97.3449,-109.4196,1;Thumper:-0.3536,-0.1048,-0.3088,78.1842,33.0364,-1.6332,1;Bladderfish:0.0323,-0.0341,-0.243,0,0,0,1;PenguinBaby:-0.0676,0.028,-0.0873,-20.1258,42.137,179.0598,0.7367";
        public static KeyBind TuneHeldItemsKey;
        public static KeyBind TuneModeKey;
        public static KeyBind TuneSaveKey;
        public static KeyBind TuneResetKey;

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

            HostKey = new KeyBind(Config, "Keys", "HostSession", KeyCode.F5, "Create a Steam lobby and open the invite dialog. Press again while hosting to re-open the invite / re-copy the lobby id.");
            // F11 was the old default, but the game takes it for screenshots: move anyone still on it to F5.
            if (HostKey.Value == KeyCode.F11) { HostKey.Value = KeyCode.F5; Logger.LogInfo("Host key moved from F11 (the game's screenshot key) to F5."); }
            InviteKey = new KeyBind(Config, "Keys", "InviteAgain", KeyCode.None, "Optional separate key to re-open the invite dialog (Host key already does this while hosting).");
            LeaveKey = new KeyBind(Config, "Keys", "LeaveSession", KeyCode.End, "Leave the current session.");
            JoinFriendKey = new KeyBind(Config, "Keys", "JoinSession", KeyCode.F4, "Join whichever Steam friend is hosting a Below Zero lobby; if none is found, joins the lobby id in the clipboard.");
            JoinClipboardKey = new KeyBind(Config, "Keys", "JoinClipboardOnly", KeyCode.None, "Optional separate key to join only from a clipboard lobby id (Join key already falls back to this).");
            RequestWorldKey = new KeyBind(Config, "Keys", "ResendWorld", KeyCode.Home, "As a client: ask the host to send their world again (normally automatic on join).");
            SendRate = Config.Bind("Network", "SendRate", 20, "Player pose updates per second (5-60).");
            MaxPlayers = Config.Bind("Network", "MaxPlayers", 8, "Lobby size when hosting, including you (2-8).");
            DisableAchievements = Config.Bind("Gameplay", "DisableAchievements", true, "Do not unlock Steam achievements while the mod is loaded.");
            RemoteDatabankAudio = Config.Bind("Story", "RemoteDatabankAudio", false, "Play the databank narration (new creature/fragment entries) when another player makes the discovery. The entry and its notification always arrive either way; the main story lines always play for everyone.");
            HeldItemScale = Config.Bind("Avatar", "HeldItemScale", 1.0f, "Overall size of the item in another player's hand. 1.0 keeps the item at its normal world size; raise or lower it if every item looks uniformly too big or too small.");
            HeldItemOffsets = Config.Bind("Avatar", "HeldItemOffsets", DefaultHeldItemOffsets, "Where each item sits in another player's hand. These are eyeballed starting points from a flat-screen tuning pass, not final - expect to adjust them, and use the TuneHeldItems key to do it. Format: 'TechType:px,py,pz,rx,ry,rz,scale' separated by ';'. Position is in metres, rotation in degrees, scale is a multiplier. An item with no entry sits at the bare hand bone.");
            TuneHeldItemsKey = new KeyBind(Config, "Keys", "TuneHeldItems", KeyCode.F9, "Flat screen only: start or stop nudging the item in another player's hand until it looks right. Set to None to disable. Everything the tuner uses is on the numpad and the F keys, so it never fights the game's own controls.");
            TuneModeKey = new KeyBind(Config, "Keys", "TuneCycleMode", KeyCode.F7, "While tuning: switch between moving, rotating and scaling.");
            FlatArmRaise = Config.Bind("Avatar", "FlatArmRaise", DefaultFlatArmRaise, "Flat-screen players only: extra turn of the right forearm (elbow bone, degrees about its own X,Y,Z) while they hold any tool, so the tool is not pointed at the floor. One setting for every tool; set it with the tuner's Arm mode.");
            if (HeldItemOffsets.Value == OldHeldItemOffsets) { HeldItemOffsets.Value = DefaultHeldItemOffsets; Logger.LogInfo("HeldItemOffsets: upgraded the old shipped defaults to the test18 tuning."); }
            if ((FlatArmRaise.Value ?? "").Replace(" ", "") == "0,0,0") { FlatArmRaise.Value = DefaultFlatArmRaise; Logger.LogInfo("FlatArmRaise: upgraded 0,0,0 to the test18 tuning."); }
            TuneSaveKey = new KeyBind(Config, "Keys", "TuneSave", KeyCode.F2, "While tuning: save this item's numbers into HeldItemOffsets.");
            // F8 is the game's bug-report key: move anyone still on it to F2.
            if (TuneSaveKey.Value == KeyCode.F8) { TuneSaveKey.Value = KeyCode.F2; Logger.LogInfo("Held item tuner save key moved from F8 (the game's bug report key) to F2."); }
            TuneResetKey = new KeyBind(Config, "Keys", "TuneReset", KeyCode.F6, "While tuning: put this item back where it started.");
            AutoSyncSave = Config.Bind("World", "AutoSyncSave", true, "On join, download the host's save and load it automatically.");
            MultiplayerSlot = Config.Bind("World", "MultiplayerSlot", "slot9990", "Save slot the host's world is written into on this machine (slot0000-slot9999). It is overwritten on every join.");
            ShowOverlay = Config.Bind("UI", "ShowOverlay", true, "Draw the status box on the desktop window (not visible in the headset).");
            CutscenePullIn = Config.Bind("Gameplay", "CutscenePullIn", true, "When another player triggers a cutscene, take you there to see it too, then bring you back to where you were. Not while piloting or in a Prawn suit.");
            AutoSprint = Config.Bind("Gameplay", "AutoSprint", false, "Always sprint when moving. Hold the game's Sprint button to walk instead. Also in Options > Multiplayer.");
            VRStickSprint = Config.Bind("VR", "FullStickSprint", false, "VR only: sprint whenever the move stick is pushed nearly all the way (like the game does for gamepads). Off by default: it leaves no way to walk at full stick. Use Auto sprint instead.");
            VRWalkSpeed = Config.Bind("VR", "WalkSpeedMultiplier", 1.5f, "VR only: walking speed on land, as a multiple of the game's walk speed (0.5-10). Does not change sprint speed.");
            VRSprintSpeed = Config.Bind("VR", "SprintSpeedMultiplier", 1.5f, "VR only: sprint speed on land, as a multiple of the game's sprint speed (0.5-10). Does not change walking speed.");
            VRSwimSpeed = Config.Bind("VR", "SwimSpeedMultiplier", 1.0f, "VR only: multiplies swimming speed (0.5-10). Raise it if VR players also fall behind underwater.");
            TestSpeed = Config.Bind("Gameplay", "TestSpeedMultiplier", 1.0f, "Playtesting: multiplies your walking, sprinting and swimming speed (1-10), on this PC only, in VR or flat screen. Leave at 1 for normal play. Read at launch; edit it with the game closed.");
            VerboseLog = Config.Bind("Debug", "Verbose", false, "Log every packet type received (spammy).");
            DiscordEnabled = Config.Bind("Discord", "Enabled", true, "Show the session in Discord (rich presence) with a Join button for friends. Needs the Discord desktop app running.");
            DiscordAppId = Config.Bind("Discord", "ApplicationId", "1550629787389792326", "Discord application id used for rich presence. Everyone in a session must use the same id. Create one at discord.com/developers/applications (New Application, name it e.g. 'Subnautica: Below Zero') and paste its Application ID here.");

            // The game's "Cleaner" scene (quit to main menu) destroys every root object that is not marked preserved,
            // including BepInEx's plugin object. Keep us (and every other plugin on this object) alive across it.
            try { if (GetComponent<SceneCleanerPreserve>() == null) gameObject.AddComponent<SceneCleanerPreserve>(); }
            catch (Exception e) { Log.LogWarning("Could not mark plugin object as preserved: " + e.Message); }

            BZMultiplayer.VR.VRSettingsGuard.Init(Config);

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
            Guard("TimeSync.Install(harmony, Net)", () => { TimeSync.Install(harmony, Net); });
            Guard("WeatherSync.Install(harmony, Net)", () => { WeatherSync.Install(harmony, Net); });
            Guard("LeviathanSync.Install(Net)", () => { LeviathanSync.Install(Net); });
            Guard("SessionExit.Install(Net)", () => { SessionExit.Install(Net); });
            Guard("StorySync.Install(harmony, Net)", () => { StorySync.Install(harmony, Net); });
            Guard("OptionsTab.Install", () => { BZMultiplayer.UI.OptionsTab.Install(harmony); });
            Guard("MainMenuEntry.Install", () => { BZMultiplayer.UI.MainMenuEntry.Install(harmony); });
            Guard("HeldItemSync.Install(Net)", () => { HeldItemSync.Install(Net); });
            Guard("CutsceneSync.Install(harmony, Net)", () => { CutsceneSync.Install(harmony, Net); });
            Guard("VehicleSync.Install(harmony, Net)", () => { VehicleSync.Install(harmony, Net); });
            Guard("SprintAssist.Install(harmony)", () => { BZMultiplayer.VR.SprintAssist.Install(harmony); });

            InventorySync.Init(Net);

            Log.LogInfo(PluginName + " " + PluginVersion + (TestBuild.Length > 0 ? "-" + TestBuild : "") + " loaded. Host: " + HostKey.Value + "  Leave: " + LeaveKey.Value + "  Join: " + JoinFriendKey.Value + "  RequestWorld: " + RequestWorldKey.Value);
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
            UI.VRWorldStatus.Update();
            StuckProbe.Update();
            WaterGuard.Update();
            JoinerSpawn.Update();
            TimeSync.Update();
            WeatherSync.Update();
            LeviathanSync.Update();
            VehicleSync.Tick();
            UI.PlayerHUD.UpdateHUD();
            BZMultiplayer.VR.SprintAssist.Tick();
            BZMultiplayer.VR.VRSettingsGuard.Tick();
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
