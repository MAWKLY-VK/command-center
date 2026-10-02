# Command Center

A companion launcher for **Command & Conquer: Generals – Zero Hour** with **Generals Online**.
It starts the game the same way the official launcher does and adds the tools players keep asking for:
a health check with one-click fixes, a map library, a replay browser, a hotkey editor that follows the
game's own rules, control bar add-ons and the settings the in-game menu cannot reach.

> Test build. Nothing here is released yet.

## Pages

The layout follows the official Generals Online launcher: a home screen with four buttons, and Tools and Options
pages with a sidebar.

| Page | What it does |
| --- | --- |
| Home | Server numbers, a line when the health check finds something, Live / Test choice, Play |
| Tools › Health | Game files, anti-cheat, wrapper and runtime DLLs, rule-changing archives, "Run as administrator" (error 740), compatibility modes, Visual C++, graphics driver, resolution, network; one-click fixes with Undo |
| Tools › Map library | The online map library and the installed maps, with previews, player counts and sizes; install by click or by dropping a `.zip` / folder |
| Tools › Replays | Every match from `Replays` and `ArchivedReplays` with map, players, armies, results and who left when; watch, keep, rename, copy, delete with Undo; turns the game's own *ArchiveReplays* option on or off |
| Tools › Hotkeys | Unit and building buttons for all 12 armies drawn like the in-game bar, plus the game keys; checks duplicates and clashes before saving |
| Tools › Add-ons | Control Bar Pro from TheSuperHackers in five sizes, checked against pinned SHA-256 hashes; hotkey letters on the button pictures |
| Options | The official launcher's sections (camera, chat, input, graphics, social, network with diagnostics, data packs, plugins) plus Launcher: language, map library source and updates |

## Ground rules

- Every change is backed up first and can be undone.
- Fixes move files to a quarantine folder instead of deleting them.
- Nothing is written into the game folder except what you ask for: hotkey files, control bar archives and quarantine moves.
- No DLL injection, no handles opened to the game process, no changes to Generals Online's own files.
- Hotkeys and control bars do not take part in the multiplayer check, so they never cause a mismatch.

## Build

Needs Windows 10 or 11 and the .NET 10 SDK.

```
dotnet build CommandCenter/CommandCenter.csproj -c Release
```

The program is `CommandCenter/bin/Release/net10.0-windows/CommandCenter.exe`.

To make a release, run `tools\publish.ps1 -Notes "What changed"`. It writes a single-file `publish\CommandCenter.exe`
and `publish\update.json`; attach both to a GitHub release tagged `v<version>`. Older copies of Command Center
then stop working until they update.

## Running

The game folder is found automatically (current folder, the program's folder, then the Steam libraries).

| Argument | Meaning |
| --- | --- |
| `--game <folder>` | Use this game folder |
| `--library-folder <folder>` | Use a local folder of maps as the map library |
| `--user-data <folder>` | Use this folder instead of `Documents\Command and Conquer Generals Zero Hour Data` (for testing) |
| `--capture <folder> --size 900x480 --pages home,tools:maps:library,options:launcher` | Save screenshots of pages without showing the window |
| `--samples` | Add sample health problems to review the layout (their fixes do nothing) |
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
- Map previews, icons and artwork are read at run time from the player's own game and Steam installation.

Command & Conquer is a trademark of Electronic Arts. This project is not affiliated with Electronic Arts
or the Generals Online team.
