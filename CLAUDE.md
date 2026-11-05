# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Command Center is a companion launcher for C&C Generals Zero Hour with Generals Online (GO): WPF, C#, .NET 10,
32-bit (x86), framework-dependent, Windows only, GPL-3.0. It sits next to GO's official launcher and never
replaces it. Version 0.4.0 is being re-engineered in place (not rewritten) into 0.5.0 and later. The approved plan is
`C:\Users\vdyxm\.claude\plans\pasted-content-id-99c3-majestic-fairy.md`; read it before starting a phase. Its
decisions are recorded in `docs/adr/`. Do not re-decide what an ADR settles. Raise it with the user instead.

The user is new to Claude Code tooling. Explain in simple Arabic and ask one question at a time.

## Commands

```
dotnet build CommandCenter.slnx -c Release
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~GameFilesTests"
```

- The tests run in a 32-bit test host (`PlatformTarget x86`), so they need the x86 .NET 10 runtime. CI installs it
  (`.github/workflows/build.yml`); on this PC it is already installed. Baseline at 0.4.0: 93 tests, all passing.
- Test parallelization is off (`CommandCenter.Tests/TestSetup.cs`) because the language (`Loc`) is global state.
- Release build: `tools\publish.ps1 -Notes "..."` writes `publish\CommandCenter.exe` (single-file, win-x86) and
  `publish\update.json`. Run it only when the user asks for a release.
- Signing key (ADR 0007): created on 2026-11-05; the public key and its SHA-256 are in ADR 0007. Never read the
  private key, and never ask for or choose its password. Run `tools/New-SigningKey.cs` again only for a key
  replacement the user asks for: start `dotnet run --file tools/New-SigningKey.cs -- <folder outside any repository>`
  in the user's terminal, and the user types the password.

### Running the app

Always run against an isolated copy of the game and a temporary data folder. Never use the real installation:

```
CommandCenter.exe --game <copy of Desktop\Test_Generals> --user-data <temp folder>
```

- `--capture <folder> --size 1100x640 --pages home,tools:maps,options:game` saves page screenshots without showing
  the window. `tools:health:questions` opens a tab, and `options:network:run` runs the network diagnostics first.
- `--samples` adds fake health problems so their layouts can be reviewed. Their fixes do nothing.
- `--update-url <url|file>` and `--go-profile <file>` read the update manifest and GO profile from a local file.
- Also available: `--library-folder` and `--map-library` (map library source), `--drawn-backdrop`, and `--lang ar|en`.
  `--lang` is removed in phase 2.

## Architecture (0.4.0, before phase 1)

- `CommandCenter/` is the only app project. `CommandCenter.Tests/` (xUnit) sees its internals
  (`InternalsVisibleTo`). `tools/MapLibraryBuilder` builds the online map catalog and is not in the solution.
- There is no MVVM yet. Pages are large code-behind files. `MainWindow` hosts a Frame with Home (`LauncherPage`),
  Tools and Options. It also provides `Toast(message, undo)` and `CaptureAsync` for `--capture`. The Tools page hosts
  sections that implement `IToolSection` (Health, Maps, Replays, Hotkeys, Add-ons). `ReadyAsync` and `ShowPart` exist
  for screenshots.
- `AppState` holds data that several pages share (server stats, health results, replays, hotkeys). It loads them in the
  background and raises change events that pages subscribe to.
- `Services/` are static classes. Phase 1 moves them into a `CommandCenter.Core` project with no WPF.
  - `GamePaths` decides the game folder in this order: `--game`, the chosen folder, the working or program folder, then
    the Steam libraries. A folder counts only if it has `WindowZH.big` and `INIZH.big`; otherwise `Game` stays empty.
    It also decides the user data folder (`--user-data`, or `Documents\Command and Conquer Generals Zero Hour Data`).
    `AppData` (`<user data>\CommandCenter`) holds backups, quarantine, settings and `errors.log`.
  - `BackupService` is the only way to change anything. `WriteFile`, `MoveToQuarantine`, `SetUserRegistryValue` and
    `ClearReadOnly` back up first and record an Undo entry. `EnsureAllowed` permits only the game folder and the
    game's Documents folder. `EnsureGameClosed` refuses writes to the game folder, `Options.ini` and GO's
    `settings.json` while the game runs. Fixes quarantine files instead of deleting them.
  - `GameFiles` reads from loose files and `.big` archives. `CsfFile`, `TgaImage`, `Dxt`, `GameImages` and
    `FactionArt` decode the game's own text and art at run time. No game art ships with the app.
  - `HealthService` (about 80 KB) has `Run()` for local checks and `RunNetworkAsync()` for network checks. Each
    `HealthResult` can carry a `Fix` that goes through `BackupService`.
  - `GameLauncher` starts the GO client the way the official launcher does and detects a running game.
  - `GoProfile` holds GO's program names, channels and service address. It is refreshed at startup from
    `go-profile.json` in this repository, validated strictly, and the last good copy is kept. `GoApi` edits GO's
    `settings.json` and `launcher.json` in place and keeps fields it does not know.
  - `UpdateService` reads `update.json` from the latest GitHub release and replaces the exe. Older copies must update
    before they run again, so every release reaches every user.
  - `Loc`, `LocText`, `{l:T}`, `Localization/ar.json`, `ArabicShaping` and `ArabicGameText` provide the Arabic UI and
    the Arabic game text. Phase 2 removes them with a mandatory migration (see the ADRs).
- Colours are hard-coded in `Pages/Views.cs`, `Theme/GoStyles.xaml` and the code-behind. The token design system
  replaces them in phase 5.
- Target layout after phase 1: `src/CommandCenter.Core`, `src/CommandCenter.App`, `tests/CommandCenter.Tests`, and
  `tools/` (plan section 2).

## Rules

- Never touch the real Steam installation
  (`C:\Program Files (x86)\Steam\steamapps\common\Command & Conquer Generals - Zero Hour`) or the real Documents game
  data during development. Use an isolated copy of `C:\Users\vdyxm\Desktop\Test_Generals` with `--game`, and a
  temporary `--user-data` folder.
- Never read or copy `GeneralsOnlineData\credentials.json`. Support bundles must exclude it.
- Keep every existing protection: backups and Undo, quarantine, the ZipSlip guard, strict `GoProfile` validation,
  editing JSON in place, no writes while the game runs, no DLL injection, and asInvoker. The only elevation allowed is
  the update replace step.
- Colours, fonts and sizes belong in `Theme/` only. No decorative or invented text or numbers. No continuous
  animation except the slow zoom of the Home image. None of the slop markers in the plan's 43-item checklist.
- Every new or redesigned page is shown to the user as real `--capture` screenshots and approved before it counts
  as done.
- Adding a NuGet package needs a discussion and an ADR. CommunityToolkit.Mvvm 8.4.2 is the only one planned.
- Check any unfamiliar API with the Microsoft Learn MCP tools before using it.
- Releases reach every user through the forced update. Before any release: a pre-release, an update test from
  0.4.0 and from the previous release with `--update-url`, `security-audit` (quick), and two offline backups of the
  signing key (ADR 0007). Keep the repository, the asset names (`CommandCenter.exe`, `update.json`) and the x86
  framework-dependent portable exe. 0.5.0 must read and migrate 0.4.0 data.
- No commit, push, pull request, release or publish unless the user asks.
- `dxwrapper.zip` is a release asset, not code. It lives in `C:\Users\vdyxm\Desktop\cc-assets\` and is never committed.

## How to work

Each phase gets its own branch. A user-approved PR is the only way into `main`.

| When | Use |
|---|---|
| Before a feature or a big change | `grill-with-docs`. Ask one question at a time, and write an ADR for anything hard to reverse |
| Turning a decision into work | `to-spec`, then `to-tickets` |
| Writing code | `tdd`, test first at the agreed boundaries |
| Moving a page to MVVM | `improve-codebase-architecture`, then characterization tests |
| A bug | `diagnosing-bugs`. Use `systematic-debugging` for intermittent ones |
| Before saying it is done | `verification-before-completion` and `dotnet test` |
| A visual change | `--capture`, then a fresh agent reviews the screenshots with `wpf-ui-review` |
| Downloads, extraction, process launch or updates | `/security-review` |
| End of a phase | `/code-review`, a short Arabic summary, one follow-up question |

- `grill-with-docs`, `to-spec`, `to-tickets`, `improve-codebase-architecture` and `setup-matt-pocock-skills` do not
  start on their own (`disable-model-invocation`). Read their `SKILL.md` under `.claude/skills/` and follow it.
- `setup-matt-pocock-skills` must run once before `to-spec` and `to-tickets`. The repository is public, so ask the
  user before anything is published to GitHub issues.
- `wpf-ui-review` does not exist yet. It is written before phase 5, from the plan's 43-item checklist plus the blur
  and mirror tests.
- Templates for test plans and UX specs are in `docs/templates/`. Third-party skills are pinned in
  `.claude/skills/SOURCES.md`.
