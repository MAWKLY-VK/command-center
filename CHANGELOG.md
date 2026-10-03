# Changelog

## Unreleased

- New look: tabs along the top (Home, Health, Maps, Replays, Hotkeys, Add-ons) with the player count and Options beside them, the game's Steam art behind the home page (a drawn background for other copies), glass panels and short transitions between pages.
- New home page: a large Play button, the client version, anti-cheat and which copy of the game is in use, the server card (players, lobbies, ping), the last match with its result and a Watch button, and tiles into the tools; health problems show as one line under Play.
- Options are saved when you leave them by a tab, the same as their Save button.
- Health: a ring shows how much of the game passed, with the passed, warning and problem counts; each problem is a card with its fix, and the passed checks are small green chips (details on hover).
- Map library: bigger map cards with the player count and size on the picture, a round tick to choose, player filters as buttons, and the actions in a bar along the bottom.
- Replays: matches as cards, and a side panel for the selected one with its map, result, date, length, starting money, players, file actions and Watch; "Archive every match" is a switch.
- Hotkeys, Add-ons and Options follow the new look; Options has a Game folder tab that shows which copy is used (Steam, another copy, or chosen by you) and lets you pick another folder.

## 0.2.1

- Checks the game's 35 original archives against the fingerprints of Zero Hour 1.04 and names the damaged, missing or changed ones (those holding the game rules cause mismatches online); on Steam a button makes Steam download only the broken files again. Fingerprints only, no game files are shipped. After the first check it takes no time.
- Finds the game and its Documents folder in any installation, not only Steam: it reads where Generals Online and the game's own installer recorded them (EA, the original discs and other copies write the same keys), and follows the data folder name the game is set to use. A folder given with --game or chosen by the player always wins.

## 0.2.0

- Play no longer leaves the game on the taskbar: Command Center stays open under the game while it starts and puts it back in front when the focus leaves it (the anti-cheat splash closing, another program taking the focus); afterwards it steps aside without taking the focus. Each launch is logged to `play.log`.
- Play shows RUNNING and waits while the game runs, whoever started it; the official launcher left open on its own no longer counts as a running game.
- Works next to Generals Online launcher 1.0.11: `settings.json` keeps fields Command Center does not know, unchanged files are not rewritten, game settings are not saved while the game runs, and Control Bar Pro installed by the official launcher is left to it.
- Arabic: check marks are no longer mirrored; resolutions, map, player and unit names, plugin details and lists of English names keep their reading order.
- New red eagle icon and home logo, made from the Generals Online launcher icon.
- Options: Language has its own tab, first in the list; the map library source setting is gone (the library always comes from the internet).
- Hotkey letters moved from Add-ons to Hotkeys; Add-ons now holds only the control bar, without the HD textures and GitHub links.
- Safety: Command Center changes files only inside the Zero Hour folder and the game's Documents folder, and never takes another folder for the game. Before, when the game was not found it used the current folder (which can be `Windows\System32`) for the health checks and fixes; Windows refused those changes, and now Command Center refuses them itself.
- When Generals Online is not installed (for example after uninstalling it), the health check and Play say so and offer the download page; when Zero Hour is not found, Play lets you choose its folder.
- About 3 MB instead of 65 MB: runs on the .NET 10 Desktop Runtime (x86) that the Generals Online launcher already installs.

## 0.1.0 – first test build

- Layout of the official Generals Online launcher: Home, Tools (Health, Map library, Replays, Hotkeys, Add-ons) and Options with a Launcher section; English and Arabic.
- Hotkey editor reads Generals Online's community patch, orders teams 1–9 then 0 and checks the engine's key rules.
- Health checks for wrapper and Visual C++ DLLs, error 740, compatibility modes, rule-changing archives and more, each fix with Undo.
- Map library: download from the online library, install by drag and drop, delete with Undo.
- Replay browser with armies, teams, watch, keep, rename and delete with Undo; results and who left when.
- Control Bar Pro in five sizes with pinned checksums.
- Layout tested at 900 × 480 (the official launcher size) and maximized.
