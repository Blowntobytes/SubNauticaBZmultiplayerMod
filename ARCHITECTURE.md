# BZMultiplayer — architecture and roadmap

Our own multiplayer for Subnautica: Below Zero. BepInEx 5 plugin, Steam relay networking, VR-first
(SubmersedVR_BZ head + hands), flat-screen players welcome. Target: full Nitrox-scale sync, built in phases
where every phase is playable on its own.

## Ground rules

- **Host-authoritative, host-star topology.** One player hosts; everyone else talks only to the host, the host
  relays. No dedicated server exe. The host's save file is the world; clients never write their own save.
- **Transport is Steam Networking Messages** (`SteamNetworkingMessages`, channel 0). Steam Datagram Relay means
  no port forwarding and no IPs. Lobbies are Steam lobbies (friends-only), joined through the Steam overlay
  ("Join Game" / invites) or `+connect_lobby` on launch. The game already inits Steam and pumps callbacks.
- **Wire format is hand-rolled binary** (`PacketWriter`/`PacketReader`). First byte is `PacketType`.
  Unreliable for poses, reliable for everything that changes world state. No JSON, no reflection serialisers.
- **SubmersedVR is a soft dependency.** All VR access goes through `VRBridge` (reflection). A player without
  SubmersedVR sends camera-as-head and no hands, and sees VR players' hands as tracked objects.
- **Every synced thing has a stable id.** Players are SteamID64. World objects will use the game's own
  `UniqueIdentifier` (`PrefabIdentifier` / `ChildObjectIdentifier` GUIDs) so ids survive save/load and match
  the host's save.

## Code map

```
src/Plugin.cs               BepInEx entry, config, hotkeys, Update/OnGUI driver
src/Net/Packets.cs          PacketType enum, PlayerPose struct, binary reader/writer
src/Net/SteamNet.cs         lobby lifecycle, sessions, send/receive/relay, roster (Hello)
src/Sync/LocalPlayerSync.cs samples local body/head/hands, sends at SendRate
src/Sync/RemotePlayer.cs    remote avatar (placeholder geometry) + smoothing
src/Sync/PlayerRegistry.cs  remote player table
src/Sync/DiverAvatar.cs     game diver model clone: animator, FinalIK hands, head/neck posing
src/Sync/BodyTemplate.cs    Harmony prefix capturing the pristine player body before SubmersedVR alters it
src/Sync/SaveSync.cs        host save slot streamed to joiners, loaded into a dedicated slot
src/Sync/WorldSync.cs       live world events (clock, items, building) via Harmony postfixes, host relays
src/VR/VRBridge.cs          reflection bridge to SubmersedVR.VRCameraRig
```

Runtime flow: `Plugin.Update` → `SteamNet.TryInit` (waits for the game's Steam init) → `Pump` (drain inbound)
→ hotkeys → `LocalPlayerSync.Update` (outbound pose) → `PlayerRegistry.Update` (move avatars).

## Phases

**Phase 0 — see each other (0.1.x–0.2.x, done).** Lobby host/join, roster, 20 Hz pose stream, placeholder
avatar with head, hands, body, nameplate. Both players load the *same* save independently (host shares the
save folder by hand for now). Nothing in the world is synced yet; this proves the pipeline in VR.

**Phase 1 — proper avatars (0.3.x, in progress).** `DiverAvatar` clones the local player's body object
(`Player.main.armsController.gameObject`: Animator + FullBodyBipedIK), strips gameplay components, drives the
animator from derived velocity/state, rotates the head bone from the remote head, and for VR players points the
FinalIK hand effectors at their tracked hand targets. Still to do: equipped tool in hand, finger curl, and
voice via the Steam voice API.

**Phase 2 — world snapshot on join (done via save streaming, 0.2.x).** The full save is streamed, so the snapshot is exact.

**Phase 2/3 first slice (0.4.0) — live events in `WorldSync`:** host clock, item pickup/drop, placeable
construction (placement + progress). Base hull pieces (BaseGhost) are next.

**Original Phase 2 notes —** Host serialises the essentials of its world for a joining client:
time of day, story goals (`StoryGoalManager`), PDA encyclopedia/known tech, placed base pieces and
furniture, dropped/placed items, vehicles. Client applies it after its own save loads. This is where
`UniqueIdentifier` mapping gets built, and it's the biggest single chunk of work.

**Phase 3 — live building and items.** Reliable events for `Builder` placement/deconstruct, base piece
construction progress, `Constructor` (mobile vehicle bay), item pickup/drop, storage container contents,
fabricator crafting. Simple ownership: host validates, everyone applies.

**Phase 4 — vehicles and docking.** Seatruck (with modules), Prawn, Snowfox: transform + input replication
with a driver-authority model (driver's client simulates, host relays; everyone else interpolates).
Docking/undocking, entering/exiting, vehicle storage.

**Phase 5 — creatures, weather, story.** Host simulates creature AI and streams positions for creatures near
any player; clients disable local AI for those ids. Weather and day/night from host. Story triggers and
cinematics replicated as events, with per-player gating where needed (e.g. only one person in a cutscene).

**Phase 6 — persistence and polish.** Client save = "connect to host"; host save carries the world.
Reconnect handling, late join mid-cutscene, interest management (don't stream far-away creatures),
in-VR lobby UI instead of the desktop overlay, Thunderstore/Nexus packaging.

## Known constraints

- The status overlay is IMGUI on the desktop mirror window. In the headset you won't see it; host/join is
  F9/F10/F11 and the Steam overlay until Phase 6 gives us a VR panel.
- Steam-only for now (Epic has no lobby/relay parity here).
- Both players need identical mod lists for any phase that touches world state.
- Phase 0 avatars are not occluded by the water fog shader the game uses; they'll look "flat" underwater.
  Phase 1 fixes this by using the game's own materials.
