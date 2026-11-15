# BZMultiplayer: notes for Claude sessions

## Where things live
- GitHub: github.com/Blowntobytes/SubNauticaBZmultiplayerMod (branch `main` only)
- Main VR PC game folder: `C:\Program Files (x86)\Steam\steamapps\common\SubnauticaZero`
  - Plugin: `BepInEx\plugins\BZMultiplayer\BZMultiplayer.dll`
  - Dev folder: `BZMultiplayer-dev\` (source, friend zips, `github\` publish files)
- Second test PC (flat screen): `C:\Steam\steamapps\common\SubnauticaZero`

## Source status
- `src/` is the real, current source (same version as `PluginVersion` in `src/Plugin.cs`). Build new versions on it.
- The newest git bundle in `BZMultiplayer-dev\github\` holds the full history with tags; clone it to start a session.

## Build
- `build.sh`: Mono `mcs` against the game's assemblies in `/root/build/libs`
  (copy from `SubnauticaZero_Data\Managed`, `BepInEx\core`). Version is `PluginVersion` in `src/Plugin.cs`.

## Versioning and release rules
- Every fix or feature gets a new version number (0.9.27, 0.9.28, ...). Never reuse one.
- Commit message: `<version>: short description`. Tag: `v<version>`. Never amend a tagged commit.
- Each version: friend all-in-one zip `BZMultiplayer-<ver>-friend-allinone.zip` into `BZMultiplayer-dev\`
  (friend zip only, no source zip), updated `src\` copied back to `BZMultiplayer-dev\src\`, CHANGELOG entry.
- The user edits README/CHANGELOG on the GitHub website: take those from origin/main before each release.
- Can't push from the cloud: deliver a git bundle + `Publish-ToGitHub.cmd` into `BZMultiplayer-dev\github\`;
  the user double-clicks it on Windows.
- Release page (user makes it on the website): bold one-line headline instruction, short bold
  "This package contains..." paragraph, `CHANGELOG:` heading with that version's bullets, friend zip as asset.
  Paste the text itself, not the file name.

## Keys
F5 host (F11 is the game's screenshot key), F4 join, Home resend world, End leave. Avoid F keys the game uses (F8 bug report, F12 screenshot).
