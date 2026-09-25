# Changelog

Only versions from 0.9.25 on are listed in detail; earlier builds were tracked in chat sessions.

## 0.11.12
- Found the real cause of the stuck VR panel: if SteamVR is already running when Steam starts the game (e.g. accepting
  an invite with the game closed), Steam launches it in flat-screen mode and SteamVR shows it on its theater screen,
  whose splash panel stays in front of the VR view. It is a SteamVR setting, not a mod bug.
- INSTALL.txt and README: bold warning for VR players (don't have SteamVR running before launching; turn off
  "Present Non-VR Applications on Theater Screen Upon Launch" in SteamVR side panel -> Dashboard).
- Removed the VR panel fix code and its VR / PanelFix config entry (it could not affect SteamVR's theater screen).
- README: removed the outdated "source status" note; `src/` builds the released DLL.

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
