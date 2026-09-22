# Changelog

Only versions from 0.9.25 on are listed in detail; earlier builds were tracked in chat sessions.

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
