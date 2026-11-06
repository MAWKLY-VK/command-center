# Command Center

A companion launcher for **Command & Conquer: Generals – Zero Hour** with **Generals Online**.
It starts the game the same way the official launcher does and adds the tools players keep asking for:
a health check with one-click fixes, a map library, a replay browser, a hotkey editor that follows the
game's own rules, control bar add-ons and the settings the in-game menu cannot reach.

> Early test release.

## Pages

Tabs along the top lead to Home, Replays, Maps, Hotkeys and Health, with Settings and Help at the top right. Home
shows art from the player's own game, the main menu's backdrop or a faction loading screen, a different one at each
start, and the other pages sit on a blurred copy of it (a drawn background for copies without the art). The Settings
sections follow the official Generals Online launcher.

| Page | What it does |
| --- | --- |
| Home | Play, the client version, anti-cheat and which copy of the game is used, the server card (players, lobbies, ping), the last match with its result and Watch, a line when the health check finds something, Live / Test choice, tiles into the tools |
| Health | A ring with how much of the game passed, then game files (any language edition), original archives checked against Zero Hour 1.04 fingerprints, the anti-cheat and its plugin, wrapper and runtime DLLs, rule-changing archives, "Run as administrator" (error 740), compatibility modes, Visual C++, graphics driver, resolution, the community data patch, Arabic game text left by version 0.4.0, network; one-click fixes with Undo. A Common questions tab answers what new players ask most (greyed-out rooms, camera height, dropped connections, anti-cheat, antivirus, GenTool, mismatches, replays, where to get the game) with buttons to the place that fixes it |
| Maps | The online map library and the installed maps as cards with previews, player counts and sizes, filters by player count; install by click or by dropping a `.zip` / folder |
| Replays | Every match from `Replays` and `ArchivedReplays` with map, players, armies, results and who left when, the selected one in a side panel; watch, keep, rename, copy, delete with Undo; turns the game's own *ArchiveReplays* option on or off |
| Hotkeys | Unit and building buttons for all 12 armies drawn like the in-game bar, the game keys, and hotkey letters on the button pictures; checks duplicates and clashes before saving |
| Add-ons | Control Bar Pro from TheSuperHackers in five sizes, checked against pinned SHA-256 hashes, with a before/after slider |
| Options | The game folder (which copy is used, pick another), then the official launcher's sections (camera, chat, input, graphics, social, network with diagnostics of ping, loss, NAT and the servers, data packs, plugins) and Launcher: updates and about |

## Ground rules

- Every change is backed up first and can be undone.
- Fixes move files to a quarantine folder instead of deleting them.
- Nothing is written into the game folder except what you ask for (hotkey files, control bar archives, fixes and quarantine moves) and the one-time removal of version 0.4.0's Arabic game text described below.
- Nothing in the game folder, `Options.ini` or `settings.json` is changed while the game is running.
- Generals Online's `settings.json` and `launcher.json` are only written when you change a setting, and only the settings you changed;
  fields Command Center does not know stay as they are. The anti-cheat choice is never changed for you.
- Changes stay inside the game folder and the game's Documents folder, also when a folder in them links somewhere else.
- No DLL injection, no handles opened to the game process, no changes to Generals Online's program files.
- Command Center starts only the programs it needs: Explorer, the firewall settings, the game's own programs and itself after an update.
- Hotkeys and control bars do not take part in the multiplayer check, so they never cause a mismatch.
- Updates, the Generals Online profile and the online map library are used only with the maintainer's signature ([ADR 0007](docs/adr/0007-ecdsa-signed-manifests.md)).
- Map zips are checked before anything is installed: their size, number of files, compression and file names. A map installed over another one keeps a backup of each file it replaces, with Undo.
- Downloads follow the Windows proxy setting. The network diagnostics still test the direct route the game uses.
- The network diagnostics contact only Generals Online's servers and Cloudflare, and run no speed test. They show your country (read over HTTPS from Cloudflare), not your internet provider. The copied reports hide the last part of IPv4 addresses, all but the start of IPv6 addresses, and your Windows user and PC names.
- A failure that shows no message, and each damaged file that was skipped, is written to `errors.log` in Command Center's folder, to attach to a bug report.

## Next to the official launcher

Command Center is a companion, not a replacement. It works alongside the Generals Online launcher:

- Play starts the game the same way and stays open under it while it starts, like the official launcher. If the focus leaves the game during the launch (for example when the anti-cheat splash closes), Command Center puts the game back in front, so a full-screen game is not left on the taskbar. Each launch is written to `play.log` in Command Center's folder.
- Play waits while the game runs, whoever started it, and the official launcher left open on its own does not count as the game.
- `settings.json` and `launcher.json` are edited in place: fields Command Center does not know, such as ones added by newer Generals Online versions, are kept, and nothing is written when nothing changed.
- Game settings are not saved while the game runs, because the game reads them only when it starts.
- Control Bar Pro installed by the official launcher stays its business: Command Center shows it and leaves those files alone, as the official launcher does with files it did not install.
- When Play cannot start the game (its program is missing, or it does not start), Command Center offers the official launcher instead.

### Following Generals Online changes

Generals Online's program names, the anti-cheat plugin name, the `launcher.json` channel key, which channels start through Easy Anti-Cheat and the service address live in [go-profile.json](go-profile.json). Every copy of Command Center reads it from this repository when it starts and keeps the last good one, so a rename by Generals Online is fixed by editing that file, without a new release. A profile is ignored unless every program is a plain `.exe` name (no folders) and the address is on `playgenerals.online` over https. From 0.5.0 it is also ignored without a valid signature in `go-profile.json.sig`, so every change to it is signed again with `tools/Sign-File.cs`.

Health compares the installed live client with the fingerprint Generals Online publishes for its own update check (`VersionManifest`, a CRC-32 of the whole file); the file is only read.

## English only

From version 0.5.0 Command Center is in English only. Version 0.4.0 could show its own interface in Arabic and put
the game's text in Arabic; both are gone.

- On the first start of the new version, and only once, Command Center looks for 0.4.0's Arabic game text in every language folder of the game. It puts the game's English text back, keeping your saved hotkeys, and moves `CommandCenterArabic.ini` to quarantine. Both are backed up first.
- If the game is running, nothing is touched, and this happens at the next start.
- Players who had the Arabic interface or the Arabic game text see a notice once, in Arabic and English. When files were changed, its Undo puts the Arabic game text back, and it is not removed again by itself.
- While Arabic game text from 0.4.0 is left, Health shows a warning with a button that removes it.
- A changed hotkey always gets the button's English name with the new key.

## Build

Needs Windows 10 or 11 and the .NET 10 SDK.

```
dotnet build CommandCenter.slnx -c Release
```

The program is `src/CommandCenter.App/bin/Release/net10.0-windows/CommandCenter.exe`. `CommandCenter.slnx` holds it,
its services (`src/CommandCenter.Core`) and the tests.

The tests run 32-bit like the program, so they also need the x86 .NET 10 runtime (the Generals Online launcher installs it):

```
dotnet test tests/CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
```

Package versions are locked in each project's `packages.lock.json`; after changing a package, run `dotnet restore` and
commit the lock files. GitHub builds and tests every push and pull request (`.github/workflows/build.yml`) on the
32-bit SDK, restores only what the lock files name, and keeps the built program for two weeks.

To make a release, run `tools\publish.ps1 -Notes "What changed"`. It writes a single-file `publish\CommandCenter.exe`
(about 3 MB) and `publish\update.json`. Then sign `update.json` with the maintainer's key (you type the password); its
signed part stays valid for one year:

```
dotnet run --file tools/Sign-File.cs -- update publish/update.json <key file>
```

Attach both files to a GitHub release tagged `v<version>`, with the manual rollback steps from [Updates](#updates) in
its notes. Older copies of Command Center then stop working until they update.

## Running

Command Center is a 32-bit program for the .NET 10 Desktop Runtime (x86), the same runtime the Generals Online
launcher installs and runs on, so it needs no installer of its own. If Windows says .NET is missing, install the
x86 Desktop Runtime from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0).

The game folder is found automatically in any installation: where Generals Online and the game's own installer
recorded it (Steam, EA, the original discs and other copies), the current folder, the program's folder and the Steam
libraries. Options › Game folder shows which copy is used and lets you pick another.

| Argument | Meaning |
| --- | --- |
| `--game <folder>` | Use this game folder |
| `--library-folder <folder>` | Use a local folder of maps as the map library |
| `--user-data <folder>` | Use this folder instead of `Documents\Command and Conquer Generals Zero Hour Data` (for testing) |
| `--capture <folder> --size 1100x640 --pages home,maps:installed,settings:game` | Save screenshots of pages without showing the window; `health:history` opens a tab, `settings:network:run` runs the diagnostics first, and `:end` shows the end of a long Settings section |
| `--samples` | Add sample health problems to review the layout (their fixes do nothing) |
| `--drawn-backdrop` | Show the drawn background that copies without the game's art get |
| `--update-url <url or file>` | Read the update manifest (`update.json`) from here instead of the latest GitHub release; a local file works for testing |
| `--go-profile <file>` | Use this Generals Online profile instead of the one in this repository (for testing); its `.sig` must sit next to it |
| `--test-key <public key file>` | Test builds only (`-p:TestKeys=true`): also trust this key and allow an update from a local file, to try the update path. Never published |

Command Center keeps its own files in `Documents\Command and Conquer Generals Zero Hour Data\CommandCenter`
(backups, quarantine, map cache, settings).

## Updates

An older copy stops working until the new version is installed. The update screen downloads the new program, checks
it and puts it in place of the old one. When Command Center's folder needs administrator rights, Windows asks for
permission for that step only.

An update is offered only when the signed part of `update.json` passes every check: the maintainer's signature, a newer
version, an expiry date one year after signing that has not passed, and a download from this repository's releases.
The downloaded program must then have that SHA-256 and that version, or nothing is replaced.

The previous version stays next to the new one as `CommandCenter.old.exe` until the new one has stayed open for 30
seconds or was closed normally. If Windows cannot start the new version at all, the previous one is put back
automatically and the update screen says why.

If the new version starts and then fails, put the previous one back by hand:

1. Close Command Center.
2. In its folder, delete `CommandCenter.exe`.
3. Rename `CommandCenter.old.exe` to `CommandCenter.exe`.

The previous version asks for the update again at its next start.

## License

Command Center is free software under the [GNU General Public License v3.0](LICENSE).
Copyright (C) 2026 Command Center contributors.

## Credits

- The interface, the Options page and the network diagnostics are based on the [Generals Online Launcher](https://github.com/GeneralsOnlineDevelopmentTeam/Launcher) by the GeneralsOnline Development Team (GPL-3.0).
- Control Bar Pro: [TheSuperHackers/GeneralsControlBar](https://github.com/TheSuperHackers/GeneralsControlBar) (MIT).
- The program icon is the Generals Online launcher icon (GPL-3.0), recoloured red and without its lettering (`toolsmake-icon.ps1`).
- The interface's line icons are drawn from [Feather](https://feathericons.com) by Cole Bemis (MIT), with square ends.
- The interface's fonts are [Barlow and Barlow Condensed](https://github.com/jpt/barlow) by The Barlow Project Authors and [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) by The JetBrains Mono Project Authors, both under the SIL Open Font License 1.1 and embedded unchanged.
- The licence texts are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- Map previews, game icons and artwork are read at run time from the player's own game.

Command & Conquer is a trademark of Electronic Arts. This project is not affiliated with Electronic Arts
or the Generals Online team.
