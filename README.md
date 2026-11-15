# BZMultiplayer

Co-op multiplayer for **Subnautica: Below Zero**, built for VR-first (SubmersedVR_BZ) with flat-screen players welcome!
A BepInEx 5 plugin using Steam networking: no port forwarding, no dedicated server, and no save files to pass
around. The host's world is sent to whoever joins.

**Status: early (0.0.1).** You and up to 7 friends can play the same world together (tested with 2 players so far). Expect rough edges, and report them
with your `BepInEx\LogOutput.log`.

## What's shared

- **Players:** each player is shown with the game's own diver model, mostly animated. VR players' hands and head are tracked,
  and whatever someone holds (scanner, knife, builder, Seaglide...) shows in their hand (not all items mapped yet). An open PDA shows in their left hand. Players more than 50 m away
  get a marker on screen or in the headset.
- **World:** day/night clock, picking up and dropping items, smashing outcrops, and storage contents (lockers, chests,
  drop pod, vehicle storage).
- **Building:** furniture and modules, plus base hull pieces (corridors, rooms, hatches, windows...), including
  deconstruction. Others see the finished piece; the ghost stays with the builder.
- **Vehicles:** the Mobile Vehicle Bay, and the Snowfox, Seatruck (with its modules) and Prawn suit. Driving,
  docking, and the other player seated in the vehicle are all shared.
- **Story:** story goals and the events they trigger, the PDA log, the encyclopedia, blueprints and scanner unlocks.
  Big story cutscenes play for everyone.
- **Inventory:** each joiner keeps their own inventory per world. The host stores it and gives it back next time
  they join.
- **Time keeps running** when someone opens their PDA or the pause menu, so no one player ever freezes the world
  for everyone else.
- **Joining:** new players spawn at the nearest base or the drop pod, whichever is closer.
- **Weather** follows the host's weather.

Creatures are not synced yet (each player sees their own fish and leviathans).

## Known issues

- VR: jumping while pushing the left stick sideways can equip random hotbar items in your right hand.
- Creatures (including leviathans) are not shared, so the same creature can be in different places for each player.
- Base building: some pieces may not appear for the other player.
- Cut scenes don't always synchronize
- Gravity is still active in a long sea truck. When the driver turns it vertical other players fall.
- Large worlds take much longer to download to all of the clients

Please report anything else on the [Issues](../../issues) page with both players' `BepInEx\LogOutput.log` (it is overwritten every launch, so copy it right after the problem).

## VR players: read this first

> **SteamVR should NOT be running before you launch the game.** Start Below Zero and let it open SteamVR itself.
>
> If SteamVR is already running when Steam starts the game (for example when you accept a Steam invite while the
> game is closed), SteamVR puts Below Zero on its **theater screen**, and a big splash panel stays stuck in front of
> you for the whole session.
>
> **Recommended:** in SteamVR, open the **side panel -> Dashboard** and turn **off**
> **"Present Non-VR Applications on Theater Screen Upon Launch"**. This option is only in SteamVR, not in Steam's
> settings.
>
> Already stuck with the panel? Quit the game, close SteamVR, and launch the game again. Or start the game first and
> join from inside it (F4 or the Multiplayer menu).

## Download and install

Get the **BZMultiplayer zip** from the [Releases](../../releases) page. It contains BepInEx and the mod.

1. VR: first install [SubmersedVR_BZ](https://github.com/jbusfield/SubmersedVR_BZ). Check that it works on its own first (you should see your hands).
2. Steam -> Library -> right-click *Subnautica: Below Zero* -> Manage -> Browse local files.
3. Extract everything in the **BZMultiplayer zip** into that folder (say yes if Windows asks to merge the BepInEx folder).
4. Launch the game once and quit, so BepInEx creates its config files.

Everyone in a session must run the same version, and you must be Steam friends.

## Play

The easiest way: the host loads their save and presses **F5**. The friend sits at the main menu and presses **F4**.
The host's world downloads (a few MB) and loads by itself. It goes into save slot `slot9990` on the joiner's machine,
which is overwritten on every join.

| Key  | Action |
|------|--------|
| F5   | Host: creates a friends-only Steam lobby (press again to reopen the invite dialog) |
| F4   | Join: from the main menu, joins the Steam friend who is hosting |
| Home | Ask the host to resend their world (normally automatic) |
| End  | Leave the session |

You don't need the keys. The main menu has a **Multiplayer** entry below Play (also found under Options ->
Multiplayer in-game) with Host / Join / Leave buttons and all the settings. Steam's "Join Game" and Discord's
**Join** button (with the Discord app running) work too.

## New QoL feature
**Auto sprint**: the first switch in Options -> Multiplayer. VR players walk and sprint 1.5x faster on
land by default, to keep up with flat-screen players. Keys and settings are in
`BepInEx\config\com.blowntobytes.bzmultiplayer.cfg`.

## Build

The source is in `src/`. `build.sh` compiles it with the Mono C# compiler against the game's own assemblies (no
NuGet, no Visual Studio). See `ARCHITECTURE.md` for how it fits together.

## Credits

[BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1), Tobey's BepInEx Pack for Below Zero
([toebeann](https://github.com/toebeann)), and [SubmersedVR_BZ](https://github.com/jbusfield/SubmersedVR_BZ) for VR.
- [JosheM] - playtester
- [void-citizen] - playtester
- [Blowntobytes] - Welder and creator of this mod
