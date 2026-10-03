# Command Center

A companion launcher for **Command & Conquer: Generals – Zero Hour** with **Generals Online**.
It starts the game the same way the official launcher does and adds the tools players keep asking for:
a health check with one-click fixes, a map library, a replay browser, a hotkey editor that follows the
game's own rules, control bar add-ons and the settings the in-game menu cannot reach.

> Early test release.

## Pages

Tabs along the top lead to each page; the player count and Options sit next to them. The home page shows the
game's Steam art (a drawn background for other copies), and the colours and Options sections follow the official
Generals Online launcher.

| Page | What it does |
| --- | --- |
| Home | Play, the client version, anti-cheat and which copy of the game is used, the server card (players, lobbies, ping), the last match with its result and Watch, a line when the health check finds something, Live / Test choice, tiles into the tools |
| Health | A ring with how much of the game passed, then game files, original archives checked against Zero Hour 1.04 fingerprints, anti-cheat, wrapper and runtime DLLs, rule-changing archives, "Run as administrator" (error 740), compatibility modes, Visual C++, graphics driver, resolution, network; one-click fixes with Undo |
| Maps | The online map library and the installed maps as cards with previews, player counts and sizes, filters by player count; install by click or by dropping a `.zip` / folder |
| Replays | Every match from `Replays` and `ArchivedReplays` with map, players, armies, results and who left when, the selected one in a side panel; watch, keep, rename, copy, delete with Undo; turns the game's own *ArchiveReplays* option on or off |
| Hotkeys | Unit and building buttons for all 12 armies drawn like the in-game bar, the game keys, and hotkey letters on the button pictures; checks duplicates and clashes before saving |
| Add-ons | Control Bar Pro from TheSuperHackers in five sizes, checked against pinned SHA-256 hashes, with a before/after slider |
| Options | Language first, then the game folder (which copy is used, pick another), then the official launcher's sections (camera, chat, input, graphics, social, network with diagnostics, data packs, plugins) and Launcher: updates and about |

## Ground rules

- Every change is backed up first and can be undone.
- Fixes move files to a quarantine folder instead of deleting them.
- Nothing is written into the game folder except what you ask for: hotkey files, control bar archives and quarantine moves.
- No DLL injection, no handles opened to the game process, no changes to Generals Online's own files.
- Hotkeys and control bars do not take part in the multiplayer check, so they never cause a mismatch.

## Next to the official launcher

Command Center is a companion, not a replacement. It works alongside the Generals Online launcher:

- Play starts the game the same way and stays open under it while it starts, like the official launcher. If the focus leaves the game during the launch (for example when the anti-cheat splash closes), Command Center puts the game back in front, so a full-screen game is not left on the taskbar. Each launch is written to `play.log` in Command Center's folder.
- Play waits while the game runs, whoever started it, and the official launcher left open on its own does not count as the game.
- `settings.json` and `launcher.json` are edited in place: fields Command Center does not know, such as ones added by newer Generals Online versions, are kept, and nothing is written when nothing changed.
- Game settings are not saved while the game runs, because the game reads them only when it starts.
- Control Bar Pro installed by the official launcher stays its business: Command Center shows it and leaves those files alone, as the official launcher does with files it did not install.

## Build

Needs Windows 10 or 11 and the .NET 10 SDK.

```
dotnet build CommandCenter/CommandCenter.csproj -c Release
```

The program is `CommandCenter/bin/Release/net10.0-windows/CommandCenter.exe`.

To make a release, run `tools\publish.ps1 -Notes "What changed"`. It writes a single-file `publish\CommandCenter.exe`
(about 3 MB) and `publish\update.json`; attach both to a GitHub release tagged `v<version>`. Older copies of
Command Center then stop working until they update.

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
| `--capture <folder> --size 1100x640 --pages home,tools:maps,options:game` | Save screenshots of pages without showing the window |
| `--samples` | Add sample health problems to review the layout (their fixes do nothing) |
| `--drawn-backdrop` | Show the background that copies without Steam art get |
| `--update-url <url or file>` | Read the update manifest (`update.json`) from here instead of the latest GitHub release; a local file works for testing |
| `--lang ar` / `--lang en` | Show Command Center in Arabic (right to left) or English for this run only; the saved language is not changed. With `--capture`, untranslated strings are listed in `missing-ar.txt` |

Command Center keeps its own files in `Documents\Command and Conquer Generals Zero Hour Data\CommandCenter`
(backups, quarantine, map cache, settings).

## License

Command Center is free software under the [GNU General Public License v3.0](LICENSE).
Copyright (C) 2026 Command Center contributors.

## Credits

- The interface, the Options page and the network diagnostics are based on the [Generals Online Launcher](https://github.com/GeneralsOnlineDevelopmentTeam/Launcher) by the GeneralsOnline Development Team (GPL-3.0).
- Control Bar Pro: [TheSuperHackers/GeneralsControlBar](https://github.com/TheSuperHackers/GeneralsControlBar) (MIT).
- The program icon is the Generals Online launcher icon (GPL-3.0), recoloured red and without its lettering (`toolsmake-icon.ps1`).
- Map previews, icons and artwork are read at run time from the player's own game and Steam installation.

Command & Conquer is a trademark of Electronic Arts. This project is not affiliated with Electronic Arts
or the Generals Online team.
