# BZMultiplayer

Multiplayer for Subnautica: Below Zero, built for VR (SubmersedVR_BZ) with flat-screen players welcome.
BepInEx 5 plugin, Steam relay networking, no port forwarding, no server exe.

**Status: Phase 2 (first slice)** — you and a friend can be in the same world and see each other as the game's diver
model (animated, VR hands driven by FinalIK), and the host's save is streamed to the joiner automatically.
Live sync so far: day/night clock, item pickup/drop, smashing resource outcrops, storage container contents (lockers, chests, drop pod storage), placing/building furniture and modules, and base hull pieces (corridors, rooms, hatches, windows...; the finished piece appears for everyone, the ghost stays with the builder), including deconstruction. Story progression (story goals and the events they trigger, PDA log, encyclopedia, blueprints, scanner unlocks) is
synced live: the main story lines play for everyone and queue behind each other, while databank narration (a new
creature or fragment) is heard only by whoever made the discovery - the others still get the entry and its
notification. Held items (scanner, knife, builder, seaglide...) show in each player's hand. Vehicles and creatures are not synced yet.
See `ARCHITECTURE.md` for the plan.

## VR players: read this first

> **SteamVR should NOT be running before you launch the game.** Start Below Zero and let it open SteamVR itself.
>
> If SteamVR is already running when Steam starts the game (for example when you accept a Steam invite while the
> game is closed), SteamVR treats Below Zero as a flat-screen game and puts it on its **theater screen**. A large
> splash panel then stays stuck in front of you for the whole session.
>
> **Recommended:** in SteamVR, open the **side panel -> Dashboard** and turn **off**
> **"Present Non-VR Applications on Theater Screen Upon Launch"**. This option is only found inside SteamVR, not in
> Steam's own settings.
>
> Already stuck with the panel? Quit the game, close SteamVR, and launch the game again. Or start the game first and
> then join from inside it (F4 or the Multiplayer menu): Steam does not relaunch a game that is already running.

## Download

Get the latest **friend all-in-one zip** from the [Releases](../../releases) page. It contains BepInEx and the mod;
extract it into your Below Zero game folder (see `INSTALL.txt`). Everyone in a session must run the same version.

## Install

1. BepInEx 5 for Below Zero (Tobey's pack). SubmersedVR_BZ if you play in VR.
2. Drop `BZMultiplayer.dll` into `BepInEx/plugins/BZMultiplayer/`.
3. Nothing else: on join the host's save is sent over Steam and loaded into a dedicated slot
   (`slot9990` by default, configurable) on the joiner's machine.

## Play

| Key  | Action |
|------|--------|
| F11  | Host — in-game, creates a friends-only Steam lobby and copies its id to the clipboard. Press again while hosting to re-open the invite dialog / re-copy the id |
| F4   | Join — from the main menu, finds the Steam friend hosting a Below Zero lobby and joins; if no friend is found, joins the lobby id in your clipboard. The host's world downloads and loads automatically |
| Home | Ask the host to resend their world (normally automatic) |
| End  | Leave the session (a joiner is returned to the main menu) |

These are the only F-row keys Below Zero leaves free (F1/F3/F5 debug, F2 input, F6 HUD, F7/F9/F10 dev tools,
F8 bug report, F12 Steam screenshot). All keys are rebindable in the config.

Easiest flow: host loads their save and presses F11; friend sits at the main menu and presses F4. The host
auto-saves, streams the slot (a few MB), and the friend's game loads it. Saves on the friend's side land in
`SNAppData\SavedGames\slot9990` inside the game folder (Steam PC). The Steam overlay invite also works if the overlay is
enabled for the game and showing on the desktop window (in VR it often isn't), and "Join Game" from the
Steam friends list works too. If the friend's game is closed, Steam launches it with the lobby id and the mod
joins automatically once in-game (VR players: see the SteamVR warning above; start the game first if SteamVR is already running).

### Multiplayer menu

The main menu has a **Multiplayer** entry directly below Play (flat screen and VR) that opens the same screen as
Options -> Multiplayer, which is also reachable from the in-game menu. It has Host / Join / Leave buttons and the settings below (max players, Steam achievements on/off,
Discord presence, auto world download, status box, update rate), so the keys are optional.

### Discord invites

With the Discord desktop app running, the mod shows your session as rich presence ("Hosting a world", party 2/8) and friends
get a **Join** button on your Discord profile card (or can "Ask to Join"; requests are accepted automatically). Clicking
Join makes their game join your Steam lobby, exactly as F4 would. The mod ships with a shared Discord application id, so nothing needs configuring; if you want your own name/icon, create an application at discord.com/developers/applications and put its Application ID in `[Discord] ApplicationId` (everyone in a session must use the same id). Discord only shows the Join button when "Share your detected activities" is on in Discord's Activity Privacy.

A status box in the top-left of the desktop window shows connection state and players. It is not visible in
the headset.

Keys, send rate and the overlay are configurable in `BepInEx/config/com.blowntobytes.bzmultiplayer.cfg`.

## Build

Source lives in `src/`. `build.sh` compiles with the Mono C# compiler against the game's own assemblies
(no NuGet, no Visual Studio). Any C# 7 compiler with the same references works.
