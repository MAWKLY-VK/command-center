# Changelog

## 0.2.0

- Play no longer sends the game to the taskbar: the game gets the foreground (as with the official launcher), and Command Center steps aside only once the game is in front, without taking the focus back.
- Play shows RUNNING and waits while the game runs, whoever started it; the official launcher left open on its own no longer counts as a running game.
- Works next to Generals Online launcher 1.0.11: `settings.json` keeps fields Command Center does not know, unchanged files are not rewritten, game settings are not saved while the game runs, and Control Bar Pro installed by the official launcher is left to it.
- Arabic: check marks are no longer mirrored; resolutions, map, player and unit names, plugin details and lists of English names keep their reading order.
- About 3 MB instead of 65 MB: runs on the .NET 10 Desktop Runtime (x86) that the Generals Online launcher already installs.

## 0.1.0 – first test build

- Layout of the official Generals Online launcher: Home, Tools (Health, Map library, Replays, Hotkeys, Add-ons) and Options with a Launcher section; English and Arabic.
- Hotkey editor reads Generals Online's community patch, orders teams 1–9 then 0 and checks the engine's key rules.
- Health checks for wrapper and Visual C++ DLLs, error 740, compatibility modes, rule-changing archives and more, each fix with Undo.
- Map library: download from the online library, install by drag and drop, delete with Undo.
- Replay browser with armies, teams, watch, keep, rename and delete with Undo; results and who left when.
- Control Bar Pro in five sizes with pinned checksums.
- Layout tested at 900 × 480 (the official launcher size) and maximized.
