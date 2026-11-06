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
dotnet test tests/CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
dotnet test tests/CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~GameFilesTests"
dotnet build tools/MapLibraryBuilder/MapLibraryBuilder.csproj -c Release
```

- The tests run in a 32-bit test host (`PlatformTarget x86`), so they need the x86 .NET 10 runtime. CI runs on the
  x86 SDK (`.github/workflows/build.yml`); on this PC the x86 runtime is already installed. Baseline at 0.4.0: 93
  tests, all passing.
- Packages are locked (`Directory.Build.props`): every project has a `packages.lock.json`, and CI restores in locked
  mode. After changing `Directory.Packages.props` or a package reference, run `dotnet restore CommandCenter.slnx` and
  `dotnet restore tools/MapLibraryBuilder/MapLibraryBuilder.csproj`, then commit the changed lock files.
- Test parallelization is off (`tests/CommandCenter.Tests/TestSetup.cs`) because the game paths and the run options are
  global state.
- `tools/MapLibraryBuilder` is not in the solution but references the App, so build it after any change to the
  services or the imaging code.
- Before/after screenshots: `tools\Capture-Pages.ps1 -Exe <exe> -Game <game copy> -Out <new folder>`, then
  `dotnet run --file tools/Compare-Captures.cs -- <before> <after>` (exit 0: no unexplained difference). Areas that
  change between runs are listed in `tools/capture-ignore.txt`. For wpf-ui-review's blur test,
  `dotnet run --file tools/Blur-Captures.cs -- <captures>` writes blurred copies to `<captures>\blur`.
- Release build: `tools\publish.ps1 -Notes "..."` writes `publish\CommandCenter.exe` (single-file, win-x86) and
  `publish\update.json`. Run it only when the user asks for a release.
- Signing key (ADR 0007): created on 2026-11-05; the public key and its SHA-256 are in ADR 0007. Never read the
  private key, and never ask for or choose its password. Run `tools/New-SigningKey.cs` again only for a key
  replacement the user asks for: start `dotnet run --file tools/New-SigningKey.cs -- <folder outside any repository>`
  in the user's terminal, and the user types the password.
- Signing files (ADR 0007, Format): start `dotnet run --file tools/Sign-File.cs -- <update|go-profile|map-catalog>
  <file> <key file>` in the user's Terminal panel, and the user types the password. It writes `<file>.sig`, or signs
  `update.json` in place with an expiry one year ahead. `--stdin-password` is only for throwaway test keys.
- Test builds: `-p:TestKeys=true` makes a build that also trusts a public key given with `--test-key <pem file>`,
  so the update path can be tried with a throwaway key. Never publish one.

### Running the app

Always run against an isolated copy of the game and a temporary data folder. Never use the real installation:

```
CommandCenter.exe --game <copy of Desktop\Test_Generals> --user-data <temp folder>
```

- `--capture <folder> --size 1100x640 --pages home,maps:installed,settings:game` saves page screenshots without
  showing the window. `health:history` opens a tab, `settings:network:run` runs the network diagnostics first, and
  `:end` shows the end of a section taller than the window (`settings:network:end`, `health:end`). `home:fix`,
  `home:update`, `home:locate` or `home:play` shows PLAY in that state, from the real decision with made-up inputs.
  `update`, `update:progress` or `update:error` shows the update screen in that state for a made-up next version; it
  covers and blocks the window, so it comes last.
- `--samples` adds fake health problems so their layouts can be reviewed. Their fixes do nothing.
- `--update-url <url|file>` and `--go-profile <file>` read the update manifest and GO profile from a local file.
- Also available: `--library-folder` and `--map-library` (map library source) and `--drawn-backdrop`. An old `--lang` is
  ignored.

## Architecture

- `src/CommandCenter.Core` holds the services, with no WPF and no desktop framework, one folder and namespace per area:
  `Content`, `Diagnostics`, `Game`, `Imaging`, `Integrity`, `Net`, `Safety`, `Settings`, `Text` and `Updates`. The
  projects import these namespaces globally. Core's internals are visible to the App and the tests.
- `src/CommandCenter.App` is the WPF program. Its assembly name stays `CommandCenter`, so the file stays
  `CommandCenter.exe`; `<Version>` in its project file is the program's version. `AppServices` applies the run options
  at startup and holds the service instances. `Imaging/` turns decoded pixels into bitmaps and draws on them, `Input/`
  maps keys, `ViewModels/` holds the view models and `Theme/` the design tokens and control styles.
- `tests/CommandCenter.Tests` (xUnit) references both. `tools/MapLibraryBuilder` builds the online map catalog and is
  not in the solution.
- Navigation (P5-D1): `MainWindow` has the tabs Home (`LauncherPage`), Replays, Maps, Hotkeys and Health (sections of
  `ToolsPage` that implement `IToolSection`), with Settings (`OptionsPage`; Add-ons is one of its sections) and Help
  (`HelpPage`) at the top right. It draws the game's art behind every page (`HomeArt`: the picture on Home, a blurred
  copy elsewhere, a drawn background without the art), holds the update screen (`UpdateOverlay`, which hides the page
  under it) and implements `IShell`: toasts, confirmations, navigation, the folder picker, the update check, stepping
  aside while the game runs. `CaptureAsync`, `ReadyAsync` and `ShowPart` exist for `--capture`.
- MVVM (CommunityToolkit.Mvvm, ADR 0004): Home (`HomeViewModel`, `PlayFlow`, `LastMatchViewModel`), Settings
  (`SettingsViewModel` with a model per section) and Health (`HealthViewModel`) are views of models that reach the
  services through interfaces (`IGameFolder`, `IAppearance`, `IAddons`, `IHealthChecks`, `IServerStatus`,
  `IGameLauncher`, `IShell`), so the tests drive them without windows. Help binds the common questions
  (`CommonQuestions`) directly. Replays, Maps and Hotkeys stay code-behind until phase 6.
- `AppState` holds data that several pages share (server stats, health results, replays, hotkeys). It loads them in the
  background and raises change events that pages subscribe to; health and the server numbers forward to
  `AppServices.Health` and `AppServices.Server`.
- Phase 5's pages reach their services as instances in `AppServices` (`GameFolder`, `Appearance`, `Addons`, `Health`,
  `Fixes`, `History`, `Server`, `Launcher`, `Sounds`). `PlayDecision` (Core/Game) decides PLAY's state (P5-D2),
  `MatchGuard` remembers the game's files after the last online match without a mismatch (P5-D3), `HealthReport`,
  `FixRunner` and `HealthChecks` (Core/Integrity) and `FixHistory` (Core/Safety) make up Health, and `GameSounds` and
  `WaveFiles` (Core/Game) read the game's interface sounds, which the App plays through winmm's PlaySound from copies
  that never move in memory.
- The other services are static classes until their pages move to MVVM in phase 6:
  - `GamePaths` decides the game folder in this order: `--game`, the chosen folder, the working or program folder, then
    the Steam libraries. A folder counts only if it has `WindowZH.big` and `INIZH.big`; otherwise `Game` stays empty.
    It also decides the user data folder (`--user-data`, or `Documents\Command and Conquer Generals Zero Hour Data`).
    `AppData` (`<user data>\CommandCenter`) holds backups, quarantine, settings and `errors.log`.
  - `BackupService` is the only way to change anything. `WriteFile`, `MoveToQuarantine`, `SetUserRegistryValue` and
    `ClearReadOnly` back up first and record an Undo entry; `CreateFile` makes a new file (a map's) with no Undo entry.
    `EnsureAllowed` permits only the game folder and the game's Documents folder, judged by `PathGuard` on the real
    path (junctions and links followed). Undo refuses journal entries that point elsewhere, and the registry is limited
    to `LayersKey`. `EnsureGameClosed` refuses writes to the game folder, `Options.ini` and GO's `settings.json` while
    the game runs. Fixes quarantine files instead of deleting them.
  - Safety (phase 3, `Core/Safety`): `ProcessRunner` is the only way to start a program (`StartKind` lists what may
    start; the update's administrator step is its only elevation). `ArchiveExtractor` extracts zips into a private
    staging folder with limits and name checks (`ExtractRules` per use). `BoundedFile` reads a whole file only up to a
    limit. Readers of game, map and replay files refuse sizes past their limits before allocating.
  - `DownloadService` (`Core/Net`) is the only way to download: one client that follows the Windows proxy, a size limit
    per kind, a stall timeout and SHA-256 checks into private temporary files. The network diagnostics stay direct.
  - `ManifestVerifier` (`Core/Integrity`) checks the signatures of `update.json`, `go-profile.json` and the map catalog
    (ADR 0007, Format). `ErrorLog` writes `errors.log` (each skipped damaged file once per run), and `Redaction` is what
    copied reports hide (both `Core/Diagnostics`).
  - `GameFiles` reads from loose files and `.big` archives. `CsfFile`, `TgaImage`, `Dxt` and `GameImages` decode the
    game's own text and art at run time into plain pixels; the App's `Imaging/` makes the bitmaps. No game art ships
    with the app.
  - `HealthService` (about 80 KB) has `Run()` for local checks and `RunNetworkAsync()` for network checks. Each
    `HealthResult` can carry a `Fix` that goes through `BackupService`.
  - `GameLauncher` starts the GO client the way the official launcher does and detects a running game.
  - `GoProfile` holds GO's program names, channels and service address. It is refreshed at startup from
    `go-profile.json` in this repository, validated strictly, and the last good copy is kept. `GoApi` edits GO's
    `settings.json` and `launcher.json` in place and keeps fields it does not know.
  - `UpdateService` reads the signed part of `update.json` from the latest GitHub release and replaces the exe. Older
    copies must update before they run again, so every release reaches every user. It takes the current version from
    the entry assembly (`CommandCenter.exe`), never from Core's own assembly. The previous exe stays as `.old.exe`
    until the new one has run for 30 seconds or closed normally, and comes back when the new one cannot start (ADR
    0009). A folder that needs administrator rights is changed by a copy started with `--replace-program` or
    `--put-back-program`, which `App.OnStartup` handles before anything else.
  - The program is in English only; texts are written in English where they are shown. `English` (Core `Text/`) words
    counts and lists. `ArabicGameTextRemoval` takes 0.4.0's Arabic game text out once, at the first start (ADR 0005),
    with a notice in Arabic and English (the only Arabic left) and Undo; Health warns while any is left.
- Theme (plan 3-c): `Theme/Tokens.xaml` holds every colour (a `Color.*` and a brush of the same name), the fonts
  (Barlow, Barlow Condensed and JetBrains Mono, embedded from `Theme/Fonts`), the type sizes, the spacing and the art
  layers; `Theme/Controls.xaml` holds the control styles built on them. `ThemeTests` checks the contrast pairs, the
  type scale, the 4 px grid, that each font resolves inside the program, and that no colour is written outside
  `Theme/` except in the phase 6 files listed in `NotYetMoved`.
- `Theme/GoStyles.xaml` keeps the old styles the phase 6 pages still use. It also sets every TextBlock without a style
  to white Segoe UI, and that reaches the text a button makes from a plain string: give button labels a TextBlock with
  `ButtonLabelStyle` (or another style) so they take the button's colours and font.

## Rules

- Never touch the real Steam installation
  (`C:\Program Files (x86)\Steam\steamapps\common\Command & Conquer Generals - Zero Hour`) or the real Documents game
  data during development. Use an isolated copy of `C:\Users\vdyxm\Desktop\Test_Generals` with `--game`, and a
  temporary `--user-data` folder.
- Never read or copy `GeneralsOnlineData\credentials.json`. Support bundles must exclude it.
- Keep every existing protection: backups and Undo, quarantine, the ZipSlip guard, strict `GoProfile` validation,
  editing JSON in place, no writes while the game runs, no DLL injection, and asInvoker. The only elevation allowed is
  the update's replace step in a folder that needs administrator rights, and putting the previous version back there
  when the new one cannot start (P3-D1).
- Colours, fonts and sizes belong in `Theme/` only. No decorative or invented text or numbers. No continuous
  animation except the slow zoom of the Home image. None of the slop markers in the plan's 43-item checklist.
- Every new or redesigned page is shown to the user as real `--capture` screenshots and approved before it counts
  as done.
- Adding a NuGet package needs a discussion and an ADR. CommunityToolkit.Mvvm 8.4.2 is the only one planned.
- Check any unfamiliar API with the Microsoft Learn MCP tools before using it.
- Releases reach every user through the forced update. Before any release: a pre-release, an update test from
  0.4.0 and from the previous release with `--update-url`, `security-audit` (quick), and two offline backups of the
  signing key (ADR 0007). `update.json` is signed with `tools/Sign-File.cs` before it is attached, and the release
  notes carry README's manual rollback steps.
- A change to `go-profile.json` here, or to `catalog.json` in zh-map-library, needs a new signature (`.sig`) from the
  user, or 0.5.0 and later ignore it. Signed files keep the exact bytes GitHub serves (`.gitattributes`). Keep the repository, the asset names (`CommandCenter.exe`, `update.json`) and the x86
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
- `setup-matt-pocock-skills` ran on 2026-11-05 (see Agent skills below). The repository is public, so ask the user
  before anything is published to GitHub issues.
- `wpf-ui-review` (`.claude/skills/wpf-ui-review/`) holds the plan's 43-item checklist plus the blur and mirror tests.
  A fresh agent runs it on the captures of the pages a change touched, and it only reports.
- Templates for test plans and UX specs are in `docs/templates/`. Third-party skills are pinned in
  `.claude/skills/SOURCES.md`.

## Agent skills

### Issue tracker

Specs and tickets are local markdown files under `.scratch/<feature>/`, which git ignores, so nothing is published.
See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: one `GLOSSARY.md` and `docs/adr/` at the repository root. See `docs/agents/domain.md`.
