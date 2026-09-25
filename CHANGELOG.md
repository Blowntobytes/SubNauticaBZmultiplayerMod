# Changelog

Only versions from 0.9.25 on are listed in detail; earlier builds were tracked in chat sessions.

## 0.11.11
- VR panel fix rebuilt with safe OpenVR calls only. 0.11.10 crashed because it picked up the game's older OpenVR
  binding (Assembly-CSharp-firstpass, IVRApplications_006) and asked native code to write into managed strings.
- The fix now uses only SteamVR.dll's binding, waits for SteamVR to finish initialising, and calls a fixed list of
  methods by exact signature (GetApplicationProcessId, CancelApplicationLaunch, IdentifyApplication, plus read-only
  compositor/dashboard status). No buffer-filling calls, no ForceReconnectProcess, no dashboard toggling.
- Runs once per join (every 2 s for 20 s). New config switch VR / PanelFix (default on) to turn it off.
- VR host still skips the Steam invite dialog (lobby id goes to the clipboard).
- 0.11.4 to 0.11.10 were VR panel test builds and were not published to GitHub.

## 0.10.0
- Per-player per-world inventory persistence: host saves each player's inventory to disk keyed by SteamID + world identity (slot + game mode). Rejoining the same world restores items; different worlds stay independent.
- Story sync fix: blueprint/creature discoveries show notifications to all players but suppress audio unless story-critical (GoalType.Story). RemoteDatabankAudio config toggle still available.
- TimeSync: prevent host from freezing time during multiplayer.
- New packet types: InventoryData (42), InventoryRequest (43).

## 0.9.26
- Fixed: the Multiplayer tab/menu entry was missing. The time-sync patch used the wrong parameter name for
  `FreezeTime.Set` (`timeScale` instead of `value`), so Harmony threw and every patch after it was skipped.
- Each feature now installs on its own. If one fails, the rest (including the menu) still load, and the log
  shows `Install failed (<feature>)`.
- New config switches: KeepWorldRunning (time sync), ShowMainMenuEntry, ShowOptionsTab.

## 0.9.25
- Known broken: Multiplayer tab missing (see 0.9.26).

## 0.8.1
- Source snapshot currently in `src/`.
