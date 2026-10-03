using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CommandCenter.Services
{
    public enum HealthStatus { Passed, Warning, Problem }

    public sealed class HealthResult
    {
        public required string Id { get; init; }
        public required string Group { get; init; }
        public required string Title { get; init; }
        public required string Detail { get; init; }
        public HealthStatus Status { get; init; }

        // Extra context shown under the detail, such as where the problem usually comes from
        public string? Note { get; init; }

        // A fix the launcher can apply and undo, with a plain description of what it will change
        public string? FixLabel { get; init; }
        public Action? Fix { get; init; }
        public string? FixPreview { get; init; }

        // Or a guide that opens a page or setting for the player
        public string? GuideLabel { get; init; }
        public string? GuideTarget { get; init; }
    }

    // Read-only checks of the game, its settings and Windows. Fixes go through BackupService so they can be undone.
    public static class HealthService
    {
        public const string GroupFiles = "Game files";
        public const string GroupWindows = "Windows";
        public const string GroupSettings = "Settings";
        public const string GroupNetwork = "Network";

        private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

        private static readonly string[] GameExecutables =
        {
            "GeneralsOnlineZH_60.exe", "GeneralsOnlineZH.exe", "GeneralsOnlineZH_TestEnvironment.exe", "EAC_LaunchGeneralsOnline.exe", "generals.exe", "game.dat",
        };

        // Archives that ship with the game; anything else in the game folder is an add-on
        private static readonly HashSet<string> KnownArchives = new(StringComparer.OrdinalIgnoreCase)
        {
            "AudioEnglishZH.big", "AudioZH.big", "EnglishZH.big", "gensecZH.big", "INIZH.big", "MapsZH.big", "Music.big",
            "MusicZH.big", "ShadersZH.big", "SpeechEnglishZH.big", "SpeechZH.big", "TerrainZH.big", "TexturesZH.big",
            "W3DEnglishZH.big", "W3DZH.big", "WindowZH.big", "PatchData.big", "PatchINI.big", "PatchWindow.big", "PatchZH.big",
        };

        // Rule data that is part of the multiplayer check; changing it causes a mismatch
        private static readonly string[] RulePaths =
        {
            @"Data\INI\Object\", @"Data\INI\Weapon.ini", @"Data\INI\Armor.ini", @"Data\INI\Locomotor.ini", @"Data\INI\Upgrade.ini",
            @"Data\INI\Science.ini", @"Data\INI\SpecialPower.ini", @"Data\INI\CommandButton.ini", @"Data\INI\CommandSet.ini",
            @"Data\INI\CommandButton\", @"Data\INI\CommandSet\",
            @"Data\INI\GameData.ini", @"Data\INI\Default\GameData.ini", @"Data\INI\PlayerTemplate.ini", @"Data\INI\AIData.ini",
            @"Data\INI\Crate.ini", @"Data\Scripts\",
        };

        // DLLs that replace parts of DirectX or Windows when placed next to the game
        private static readonly Dictionary<string, string> WrapperDlls = new(StringComparer.OrdinalIgnoreCase)
        {
            ["d3d8.dll"] = "DirectX 8 replacement (GenTool, dgVoodoo or DXVK)",
            ["d3d9.dll"] = "DirectX 9 replacement",
            ["dxgi.dll"] = "DXGI replacement (ReShade or DXVK)",
            ["ddraw.dll"] = "DirectDraw replacement",
            ["dinput8.dll"] = "input hook",
            ["dsound.dll"] = "sound hook",
            ["version.dll"] = "loader hook",
            ["winmm.dll"] = "loader hook",
        };

        // Visual C++ runtime files that older repacks and patchers copy into the game folder.
        // Windows loads these instead of the up-to-date system copy.
        private static readonly string[] RuntimeDlls =
        {
            "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll", "vcruntime140.dll", "vcruntime140_1.dll",
            "concrt140.dll", "ucrtbase.dll",
        };

        private static readonly string[] HarmfulCompatModes =
        {
            "WIN95", "WIN98", "NT4SP5", "WIN2000", "WINXP", "WINXPSP2", "WINXPSP3", "VISTARTM", "VISTASP1", "VISTASP2",
            "WIN7RTM", "WIN8RTM", "WIN81RTM", "256COLOR", "16BITCOLOR", "640X480",
        };

        // The Visual C++ 2015-2022 runtime version Generals Online is built against (14.44.35211)
        private const int MinVcMinor = 44;

        public static List<HealthResult> Run()
        {
            string game = GamePaths.Game;
            var results = new List<HealthResult>();
            void Add(Func<HealthResult?> check)
            {
                try
                {
                    if (check() is { } result)
                        results.Add(result);
                }
                catch (Exception ex)
                {
                    results.Add(new HealthResult { Id = "error:" + results.Count, Group = GroupFiles, Title = "A check could not run", Detail = ex.Message, Status = HealthStatus.Warning });
                }
            }
            void AddMany(Func<IEnumerable<HealthResult>> checks)
            {
                try { results.AddRange(checks()); }
                catch (Exception ex) { results.Add(new HealthResult { Id = "error:" + results.Count, Group = GroupFiles, Title = "A check could not run", Detail = ex.Message, Status = HealthStatus.Warning }); }
            }

            Add(() => CheckGameFiles(game));
            Add(() => CheckInstallPath(game));
            Add(() => CheckWritable(game));
            Add(CheckGameRunning);
            AddMany(() => CheckRuntimeCopies(game));
            Add(() => CheckDbgHelp(game));
            AddMany(() => CheckWrappers(game));
            AddMany(() => CheckExtraArchives(game));
            Add(() => CheckReadOnlyGameFiles(game));
            Add(() => CheckEacSettings(game));

            AddMany(() => CheckAdminFlag(game));
            Add(() => CheckCompatibilityMode(game));
            AddMany(() => CheckDpiOverride(game));
            Add(CheckVisualCpp);
            Add(CheckEasyAntiCheat);
            Add(CheckGraphics);

            // Before the Options.ini fixes, so "Fix all" can write to a file that was read-only
            Add(CheckReadOnlySettings);
            Add(CheckOptionsFile);
            Add(CheckResolution);
            Add(CheckNetworkAddress);
            Add(CheckGoSettings);
            Add(CheckDocumentsFolder);
            Add(CheckDataFolderName);
            Add(() => CheckDocumentsPath(game));
            return results;
        }

        // ── GAME FILES ──

        private static HealthResult CheckGameFiles(string game)
        {
            string[] required = { "GeneralsOnlineZH_60.exe", "EAC_LaunchGeneralsOnline.exe", "xaudio2_9redist.dll", "INIZH.big", "EnglishZH.big", "W3DZH.big", "WindowZH.big" };
            var missing = required.Where(f => !File.Exists(Path.Combine(game, f))).ToList();
            bool steam = File.Exists(Path.Combine(game, "steam_appid.txt"));

            return missing.Count == 0
                ? new HealthResult { Id = "files", Group = GroupFiles, Title = "Generals Online files complete", Detail = "GeneralsOnlineZH_60.exe, the anti-cheat launcher and xaudio2_9redist.dll are in place", Status = HealthStatus.Passed }
                : new HealthResult
                {
                    Id = "files", Group = GroupFiles, Title = "Game files are missing", Status = HealthStatus.Problem,
                    Detail = "Missing: " + string.Join(", ", missing),
                    Note = missing.Contains("GeneralsOnlineZH_60.exe") ? "Antivirus programs sometimes remove it. Restore it from quarantine or reinstall Generals Online." : null,
                    GuideLabel = steam ? "Verify in Steam" : null,
                    GuideTarget = steam ? "steam://validate/" + GamePaths.SteamAppId : null,
                };
        }

        private static HealthResult CheckInstallPath(string game)
        {
            bool ascii = game.All(c => c < 128);
            return ascii
                ? new HealthResult { Id = "path", Group = GroupFiles, Title = "Install path is plain English", Detail = "Easy Anti-Cheat can start from this folder", Status = HealthStatus.Passed }
                : new HealthResult
                {
                    Id = "path", Group = GroupFiles, Title = "Install path has non-English letters", Status = HealthStatus.Problem,
                    Detail = "Easy Anti-Cheat stops with \"Invalid game executable path\". Move the game to a folder such as C:\\Games",
                };
        }

        private static HealthResult CheckWritable(string game)
        {
            if (CanWrite(game))
                return new HealthResult { Id = "writable", Group = GroupFiles, Title = "Game folder can be updated", Detail = "Generals Online can install updates without administrator rights", Status = HealthStatus.Passed };
            return new HealthResult
            {
                Id = "writable", Group = GroupFiles, Title = "Game folder is read-only", Status = HealthStatus.Warning,
                Detail = "Updates need administrator rights here, which leads people to set \"Run as administrator\" and then hit error 740",
                Note = "Installing the game outside Program Files avoids this.",
            };
        }

        private static bool CanWrite(string folder)
        {
            try
            {
                using (new FileStream(Path.Combine(folder, $"cc-write-test-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static HealthResult? CheckGameRunning()
        {
            var running = GameLauncher.RunningGames();
            return running.Count == 0 ? null : new HealthResult
            {
                Id = "running", Group = GroupFiles, Title = "The game is running", Status = HealthStatus.Warning,
                Detail = "Close the game before applying fixes: " + string.Join(", ", running),
            };
        }

        private static IEnumerable<HealthResult> CheckRuntimeCopies(string game)
        {
            var found = RuntimeDlls.Select(d => Path.Combine(game, d)).Where(File.Exists).ToList();
            if (found.Count == 0)
            {
                yield return new HealthResult { Id = "runtimecopies", Group = GroupFiles, Title = "No old Visual C++ files in the game folder", Detail = "Windows uses its own up-to-date runtime", Status = HealthStatus.Passed };
                yield break;
            }

            foreach (string path in found)
            {
                string name = Path.GetFileName(path);
                string version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown version";
                string target = path;
                yield return new HealthResult
                {
                    Id = "runtime:" + name, Group = GroupFiles, Title = $"Old Visual C++ file inside the game folder", Status = HealthStatus.Problem,
                    Detail = $"{name} ({version}) is loaded instead of the system copy, so the game stops with \"Entry Point Not Found\"",
                    Note = "Usually left by GenPatcher or an old repack.",
                    FixLabel = "Quarantine",
                    FixPreview = $"Moves {name} to the launcher's quarantine folder. Undo puts it back.",
                    Fix = () => BackupService.MoveToQuarantine("Quarantined " + name, target, "Old Visual C++ file"),
                };
            }
        }

        // Generals Online loads dbghelp.dll when it starts (crash reports), and Windows takes the copy in the game folder first.
        // The original game shipped an old one; GenPatcher renames it to dbghelp.dll.bak for the same reason.
        private static HealthResult CheckDbgHelp(string game)
        {
            string path = Path.Combine(game, "dbghelp.dll");
            if (!File.Exists(path))
                return new HealthResult { Id = "dbghelp", Group = GroupFiles, Title = "No old dbghelp.dll", Detail = "Generals Online uses the crash report helper that comes with Windows", Status = HealthStatus.Passed };

            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "dbghelp.dll");
            var local = FileVersionOf(path);
            if (File.Exists(system) && local >= FileVersionOf(system))
                return new HealthResult { Id = "dbghelp", Group = GroupFiles, Title = "dbghelp.dll is up to date", Detail = $"The copy in the game folder ({local}) is as new as the one in Windows", Status = HealthStatus.Passed };

            return new HealthResult
            {
                Id = "dbghelp", Group = GroupFiles, Title = "Old dbghelp.dll inside the game folder", Status = HealthStatus.Problem,
                Detail = $"dbghelp.dll ({local}) is loaded instead of the Windows copy. It is too old for Generals Online, which then does not start (\"Entry Point Not Found\") or crashes",
                Note = "It comes with the original game files. GenPatcher renames it to dbghelp.dll.bak for the same reason.",
                FixLabel = "Quarantine",
                FixPreview = "Moves dbghelp.dll to the launcher's quarantine folder so Windows' own copy is used. Undo puts it back.",
                Fix = () => BackupService.MoveToQuarantine("Quarantined dbghelp.dll", path, "Old crash report helper"),
            };
        }

        private static Version FileVersionOf(string path)
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }

        private static IEnumerable<HealthResult> CheckWrappers(string game)
        {
            var found = new List<HealthResult>();
            foreach (var (dll, what) in WrapperDlls)
            {
                string path = Path.Combine(game, dll);
                if (!File.Exists(path))
                    continue;
                var info = FileVersionInfo.GetVersionInfo(path);
                string product = (info.ProductName ?? info.FileDescription ?? "").Trim();
                string label = product.Length > 0 ? product : what;
                if (dll.Equals("d3d8.dll", StringComparison.OrdinalIgnoreCase))
                {
                    // Generals Online loads DirectX 8 from the Windows folder only, so GenTool, dgVoodoo or DXVK here
                    // still work for the original game and need no fix
                    found.Add(new HealthResult
                    {
                        Id = "wrapper:" + dll, Group = GroupFiles, Title = "d3d8.dll is not used by Generals Online", Status = HealthStatus.Passed,
                        Detail = $"{label}. Generals Online loads the DirectX 8 that comes with Windows, so this file only affects the original game",
                    });
                    continue;
                }
                found.Add(new HealthResult
                {
                    Id = "wrapper:" + dll, Group = GroupFiles, Title = $"{dll} replaces part of the game", Status = HealthStatus.Warning,
                    Detail = $"{label}. Generals Online does not need it and the anti-cheat can refuse to start with it",
                    FixLabel = "Quarantine",
                    FixPreview = $"Moves {dll} to the launcher's quarantine folder. Undo puts it back.",
                    Fix = () => BackupService.MoveToQuarantine("Quarantined " + dll, path, label),
                });
            }
            foreach (string asi in Directory.EnumerateFiles(game, "*.asi"))
            {
                string name = Path.GetFileName(asi);
                found.Add(new HealthResult
                {
                    Id = "asi:" + name, Group = GroupFiles, Title = $"Script plugin {name}", Status = HealthStatus.Warning,
                    Detail = "ASI plugins are injected into the game and are blocked by the anti-cheat",
                    FixLabel = "Quarantine",
                    FixPreview = $"Moves {name} to the launcher's quarantine folder. Undo puts it back.",
                    Fix = () => BackupService.MoveToQuarantine("Quarantined " + name, asi, "ASI plugin"),
                });
            }
            if (found.All(f => f.Status == HealthStatus.Passed))
                found.Add(new HealthResult
                {
                    Id = "wrappers", Group = GroupFiles, Title = "No wrapper DLLs", Status = HealthStatus.Passed,
                    Detail = found.Count == 0
                        ? "No GenTool, dgVoodoo, ReShade or other DirectX replacement in the game folder"
                        : "Nothing else in the game folder replaces part of DirectX or Windows",
                });
            return found;
        }

        private static IEnumerable<HealthResult> CheckExtraArchives(string game)
        {
            var found = new List<HealthResult>();
            foreach (string archive in GameTree(game, "*.big"))
            {
                string name = Path.GetFileName(archive);
                string relative = Path.GetRelativePath(game, archive);
                bool inSubfolder = relative != name;
                if (inSubfolder ? IsPartOfInstall(game, archive, relative) : KnownArchives.Contains(name))
                    continue;

                List<string> entries;
                try { entries = ReadArchiveNames(archive); }
                catch { continue; }

                bool changesRules = entries.Any(e => RulePaths.Any(r => e.StartsWith(r, StringComparison.OrdinalIgnoreCase)));
                if (!changesRules)
                    continue;

                string path = archive;
                found.Add(new HealthResult
                {
                    Id = "archive:" + relative, Group = GroupFiles, Title = $"{relative} changes unit rules", Status = HealthStatus.Problem,
                    Detail = inSubfolder
                        ? "The game also loads archives from folders inside the game folder. Other players do not have it, so online games end with \"The host has modified INI files\""
                        : "Other players do not have it, so online games end with \"The host has modified INI files\"",
                    FixLabel = "Quarantine",
                    FixPreview = $"Moves {relative} to the launcher's quarantine folder. Undo puts it back.",
                    Fix = () => BackupService.MoveToQuarantine("Quarantined " + name, path, "Caused mismatches"),
                });
            }

            foreach (string rule in RulePaths)
            {
                string loose = Path.Combine(game, rule.TrimEnd('\\'));
                bool present = File.Exists(loose) || (Directory.Exists(loose) && Directory.EnumerateFiles(loose, "*.ini", SearchOption.AllDirectories).Any());
                if (!present || rule.StartsWith(@"Data\Scripts", StringComparison.OrdinalIgnoreCase))
                    continue;
                found.Add(new HealthResult
                {
                    Id = "loose:" + rule, Group = GroupFiles, Title = "Loose rule files", Status = HealthStatus.Problem,
                    Detail = $"{rule.TrimEnd('\\')} overrides unit rules and causes \"The host has modified INI files\"",
                    Note = "Remove it by hand if it belongs to a mod you still want for offline play.",
                });
            }

            if (found.Count == 0)
                found.Add(new HealthResult { Id = "archives", Group = GroupFiles, Title = "No add-ons that change unit rules", Detail = "Prevents \"The host has modified INI files\"", Status = HealthStatus.Passed });
            return found;
        }

        // CD installs and some repacks copy every file as read-only. The Generals Online installer (Inno Setup) asks
        // before it overwrites a read-only file, and a file it skips stays on the old version.
        private static HealthResult CheckReadOnlyGameFiles(string game)
        {
            var locked = GameTree(game, "*").Where(IsReadOnly).ToList();
            if (locked.Count == 0)
                return new HealthResult { Id = "readonly:game", Group = GroupFiles, Title = "No read-only game files", Detail = "Updates can replace every file in the game folder", Status = HealthStatus.Passed };

            string names = NameList(locked.Select(f => Path.GetRelativePath(game, f)).ToList());
            bool writable = CanWrite(game);
            return new HealthResult
            {
                Id = "readonly:game", Group = GroupFiles, Title = "Read-only files in the game folder", Status = HealthStatus.Warning,
                Detail = $"{Plural(locked.Count, "file is", "files are")} marked read-only: {names}. Generals Online updates stop to ask about them, and a skipped file stays on the old version",
                Note = writable
                    ? "Old CD installs and repacks copy files this way. GenPatcher clears this flag too."
                    : "Changing them needs administrator rights here: right-click the game folder › Properties, untick \"Read-only\" and apply it to all files.",
                FixLabel = writable ? "Fix" : null,
                FixPreview = writable ? $"Clears the read-only flag on {Plural(locked.Count, "file", "files")} in the game folder. Their contents stay the same. Undo sets the flag again." : null,
                Fix = writable ? () => BackupService.ClearReadOnly("Cleared read-only game files", game, locked, $"Game folder · {Plural(locked.Count, "file", "files")}") : null,
                GuideLabel = writable ? null : "Show folder",
                GuideTarget = writable ? null : "select:" + game,
            };
        }

        private static bool IsReadOnly(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

        private static string Plural(int n, string one, string many) => n == 1 ? "1 " + one : $"{n} {many}";

        // "a, b, c and 4 more"
        private static string NameList(IReadOnlyList<string> names, int show = 3) =>
            names.Count == 1 ? names[0]
            : names.Count <= show ? string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
            : string.Join(", ", names.Take(show)) + $" and {names.Count - show} more";

        private static HealthResult? CheckEacSettings(string game)
        {
            string settings = Path.Combine(game, "EasyAntiCheat", "Settings.json");
            if (!File.Exists(settings))
                return null;
            string? exe = null;
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings)))
            {
                if (doc.RootElement.TryGetProperty("executable", out var value))
                    exe = value.GetString();
            }
            if (string.IsNullOrEmpty(exe) || File.Exists(Path.Combine(game, exe)))
                return new HealthResult { Id = "eacexe", Group = GroupFiles, Title = "Anti-cheat finds the game", Detail = $"EasyAntiCheat starts {exe}", Status = HealthStatus.Passed };
            return new HealthResult
            {
                Id = "eacexe", Group = GroupFiles, Title = "Anti-cheat cannot find the game", Status = HealthStatus.Problem,
                Detail = $"EasyAntiCheat looks for {exe}, which is missing, so it stops with \"Invalid game executable path\"",
                Note = "Reinstall Generals Online, or restore the file if an antivirus removed it.",
            };
        }

        // Files the game finds the way it looks for archives: the game folder and every folder below it,
        // skipping folder names with a dot like the game's own "*." folder search does
        private static List<string> GameTree(string folder, string pattern)
        {
            var files = Directory.EnumerateFiles(folder, pattern).ToList();
            foreach (string sub in Directory.EnumerateDirectories(folder))
            {
                if (Path.GetFileName(sub).Contains('.') || new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                try { files.AddRange(GameTree(sub, pattern)); }
                catch (UnauthorizedAccessException) { }
            }
            return files;
        }

        // Archives below the game folder that every player of the same edition has
        private static bool IsPartOfInstall(string game, string archive, string relative)
        {
            // Steam keeps the original Generals archives in ZH_Generals
            if (relative.StartsWith(@"ZH_Generals\", StringComparison.OrdinalIgnoreCase))
                return true;
            // Older editions ship a second INIZH.big here; Generals Online skips it
            if (relative.Equals(@"Data\INI\INIZH.big", StringComparison.OrdinalIgnoreCase))
                return true;
            // A copy of one of the game's own archives
            string original = Path.Combine(game, Path.GetFileName(archive));
            return KnownArchives.Contains(Path.GetFileName(archive)) && File.Exists(original) && new FileInfo(original).Length == new FileInfo(archive).Length;
        }

        private static List<string> ReadArchiveNames(string archive)
        {
            using var reader = new BinaryReader(File.OpenRead(archive));
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) is not ("BIGF" or "BIG4"))
                return new List<string>();
            reader.ReadUInt32();
            byte[] c = reader.ReadBytes(4);
            uint count = (uint)(c[0] << 24 | c[1] << 16 | c[2] << 8 | c[3]);
            reader.ReadUInt32();
            var names = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                reader.ReadBytes(8);
                var sb = new StringBuilder();
                for (byte b = reader.ReadByte(); b != 0; b = reader.ReadByte())
                    sb.Append((char)b);
                names.Add(sb.ToString());
            }
            return names;
        }

        // ── WINDOWS ──

        // "Run as administrator" on the game makes Windows refuse to start it from a normal launcher (error 740)
        private static IEnumerable<HealthResult> CheckAdminFlag(string game)
        {
            var user = LayerEntries(Registry.CurrentUser, game).Where(e => HasToken(e.Data, "RUNASADMIN")).ToList();
            var machine = LayerEntries(MachineHive, game).Where(e => HasToken(e.Data, "RUNASADMIN")).ToList();

            if (user.Count == 0 && machine.Count == 0)
            {
                yield return new HealthResult { Id = "admin", Group = GroupWindows, Title = "\"Run as administrator\" is off", Detail = "Windows starts the game without error 740", Status = HealthStatus.Passed };
                yield break;
            }

            if (user.Count > 0)
            {
                string files = string.Join(", ", user.Select(u => Path.GetFileName(u.Name)));
                yield return new HealthResult
                {
                    Id = "admin", Group = GroupWindows, Title = "\"Run as administrator\" is set on the game", Status = HealthStatus.Warning,
                    Detail = "Windows blocks the game with error 740 \"The requested operation requires elevation\"",
                    Note = $"Flag RUNASADMIN on {files}",
                    FixLabel = "Fix",
                    FixPreview = $"Removes RUNASADMIN from the compatibility settings of {files}. Other settings stay.",
                    Fix = () =>
                    {
                        foreach (var (name, _) in user)
                            RemoveLayerFlags("Turned off \"Run as administrator\"", name, "RUNASADMIN");
                    },
                };
            }
            if (machine.Count > 0)
            {
                yield return new HealthResult
                {
                    Id = "adminall", Group = GroupWindows, Title = "\"Run as administrator\" is set for all users", Status = HealthStatus.Warning,
                    Detail = "Windows blocks the game with error 740. This setting needs administrator rights to change",
                    Note = $"Right-click {Path.GetFileName(machine[0].Name)} › Properties › Compatibility › Change settings for all users, then untick \"Run this program as an administrator\".",
                    GuideLabel = "Show file",
                    GuideTarget = "select:" + machine[0].Name,
                };
            }
        }

        private static HealthResult CheckCompatibilityMode(string game)
        {
            var found = LayerEntries(Registry.CurrentUser, game)
                .Where(e => e.Data.Split(' ').Any(t => HarmfulCompatModes.Contains(t.ToUpperInvariant())))
                .ToList();

            if (found.Count == 0)
                return new HealthResult { Id = "compat", Group = GroupWindows, Title = "No old compatibility mode", Detail = "Right for Windows 10 and 11", Status = HealthStatus.Passed };

            string files = string.Join(", ", found.Select(f => Path.GetFileName(f.Name)));
            return new HealthResult
            {
                Id = "compat", Group = GroupWindows, Title = "Old Windows compatibility mode", Status = HealthStatus.Warning,
                Detail = $"An old Windows mode is set on {files}; this causes crashes, black screens and the DirectX 8.1 message",
                FixLabel = "Fix",
                FixPreview = $"Removes the old Windows mode from {files}. Other settings stay.",
                Fix = () =>
                {
                    foreach (var (name, _) in found)
                        RemoveLayerFlags("Removed old compatibility mode", name, HarmfulCompatModes);
                },
            };
        }

        // "Override high DPI scaling behavior" set to System (DPIUNAWARE) or System (Enhanced) (GDIDPISCALING DPIUNAWARE).
        // Generals Online declares itself DPI aware (per monitor), so Windows should leave the scaling to the game.
        private static readonly string[] DpiOverrides = { "DPIUNAWARE", "GDIDPISCALING" };

        private static IEnumerable<HealthResult> CheckDpiOverride(string game)
        {
            var user = LayerEntries(Registry.CurrentUser, game).Where(e => DpiOverrides.Any(t => HasToken(e.Data, t))).ToList();
            var machine = LayerEntries(MachineHive, game).Where(e => DpiOverrides.Any(t => HasToken(e.Data, t))).ToList();
            return DpiResults(user, machine);
        }

        private static IEnumerable<HealthResult> DpiResults(List<(string Name, string Data)> user, List<(string Name, string Data)> machine)
        {
            const string detail = "Windows is set to scale the game picture itself (\"Override high DPI scaling\" › System). Generals Online scales itself, so on a display set above 100% the game looks blurry and menus or the mouse can be out of line";
            if (user.Count == 0 && machine.Count == 0)
            {
                yield return new HealthResult { Id = "dpi", Group = GroupWindows, Title = "No DPI scaling override", Detail = "Generals Online handles high-resolution displays itself", Status = HealthStatus.Passed };
                yield break;
            }
            if (user.Count > 0)
            {
                string files = string.Join(", ", user.Select(u => Path.GetFileName(u.Name)));
                yield return new HealthResult
                {
                    Id = "dpi", Group = GroupWindows, Title = "DPI scaling is overridden for the game", Status = HealthStatus.Warning,
                    Detail = detail,
                    Note = $"Set on {files}",
                    FixLabel = "Fix",
                    FixPreview = $"Removes the DPI override (DPIUNAWARE, GDIDPISCALING) from the compatibility settings of {files}. Other settings stay.",
                    Fix = () =>
                    {
                        foreach (var (name, _) in user)
                            RemoveLayerFlags("Removed DPI scaling override", name, DpiOverrides);
                    },
                };
            }
            if (machine.Count > 0)
            {
                yield return new HealthResult
                {
                    Id = "dpiall", Group = GroupWindows, Title = "DPI scaling is overridden for all users", Status = HealthStatus.Warning,
                    Detail = detail + ". This setting needs administrator rights to change",
                    Note = $"Right-click {Path.GetFileName(machine[0].Name)} › Properties › Compatibility › Change settings for all users › Change high DPI settings, then untick \"Override high DPI scaling behavior\".",
                    GuideLabel = "Show file",
                    GuideTarget = "select:" + machine[0].Name,
                };
            }
        }

        // Removes flags from one program's compatibility settings, starting from what is there now
        // so several fixes on the same program keep each other's changes
        private static void RemoveLayerFlags(string title, string program, params string[] flags)
        {
            string? data;
            using (var key = Registry.CurrentUser.OpenSubKey(LayersKey))
                data = key?.GetValue(program) as string;
            if (data == null || !flags.Any(f => HasToken(data, f)))
                return;
            string? keep = data;
            foreach (string flag in flags)
                keep = keep == null ? null : Without(keep, flag);
            BackupService.SetUserRegistryValue(title, LayersKey, program, keep, Path.GetFileName(program) + " · registry");
        }

        // The machine-wide compatibility settings live in the 64-bit view, which this 32-bit program has to ask for
        private static readonly RegistryKey MachineHive =
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

        private static List<(string Name, string Data)> LayerEntries(RegistryKey hive, string game)
        {
            var found = new List<(string, string)>();
            using var key = hive.OpenSubKey(LayersKey);
            if (key == null)
                return found;
            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string data)
                    continue;
                bool isGame = GameExecutables.Any(exe => string.Equals(name, Path.Combine(game, exe), StringComparison.OrdinalIgnoreCase))
                              || (name.StartsWith(game, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (isGame)
                    found.Add((name, data));
            }
            return found;
        }

        private static bool HasToken(string data, string token) =>
            data.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase));

        // Removes one flag; returns null when only the "~" or "$" markers would be left
        private static string? Without(string data, string token)
        {
            var keep = data.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !t.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList();
            return keep.All(t => t == "~" || t == "$") ? null : string.Join(" ", keep);
        }

        private static HealthResult CheckVisualCpp()
        {
            // The x86 runtime registers itself in the 32-bit view (WOW6432Node on 64-bit Windows)
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X86");
            bool installed = key?.GetValue("Installed") is int i && i == 1;
            string version = (key?.GetValue("Version") as string ?? "").TrimStart('v');
            int minor = key?.GetValue("Minor") is int m ? m : 0;
            const string guide = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist";

            if (!installed)
                return new HealthResult
                {
                    Id = "vcredist", Group = GroupWindows, Title = "Visual C++ runtime is missing", Status = HealthStatus.Problem,
                    Detail = "The game does not start without the Visual C++ 2015-2022 x86 runtime",
                    GuideLabel = "Download", GuideTarget = guide,
                };
            if (minor < MinVcMinor)
                return new HealthResult
                {
                    Id = "vcredist", Group = GroupWindows, Title = "Visual C++ runtime is out of date", Status = HealthStatus.Problem,
                    Detail = $"Version {version} is installed; Generals Online needs 14.{MinVcMinor} or newer, otherwise \"Entry Point Not Found\" appears",
                    GuideLabel = "Download", GuideTarget = guide,
                };
            return new HealthResult { Id = "vcredist", Group = GroupWindows, Title = "Visual C++ 2015-2022 x86", Detail = $"{version}, new enough for Generals Online", Status = HealthStatus.Passed };
        }

        private static HealthResult CheckEasyAntiCheat()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EasyAntiCheat_EOS");
            return key != null
                ? new HealthResult { Id = "eac", Group = GroupWindows, Title = "Easy Anti-Cheat service", Detail = "Installed and ready", Status = HealthStatus.Passed }
                : new HealthResult { Id = "eac", Group = GroupWindows, Title = "Easy Anti-Cheat service is not installed yet", Detail = "It installs the first time you play with Easy Anti-Cheat selected", Status = HealthStatus.Warning };
        }

        private static HealthResult? CheckGraphics()
        {
            var names = new List<string>();
            using (var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
            {
                if (classKey == null)
                    return null;
                foreach (string sub in classKey.GetSubKeyNames().Where(s => s.All(char.IsDigit)))
                {
                    using var adapter = classKey.OpenSubKey(sub);
                    if (adapter?.GetValue("DriverDesc") is string desc && !names.Contains(desc))
                        names.Add(desc);
                }
            }
            if (names.Count == 0)
                return null;
            var real = names.Where(n => !n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) && !n.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                                        && !n.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Contains("Parsec", StringComparison.OrdinalIgnoreCase)).ToList();
            return real.Count > 0
                ? new HealthResult { Id = "gpu", Group = GroupWindows, Title = "Graphics driver", Detail = string.Join(" · ", real), Status = HealthStatus.Passed }
                : new HealthResult
                {
                    Id = "gpu", Group = GroupWindows, Title = "No graphics driver installed", Status = HealthStatus.Problem,
                    Detail = "Windows uses \"Microsoft Basic Display Adapter\", so the game cannot start DirectX",
                    GuideLabel = "Windows Update", GuideTarget = "ms-settings:windowsupdate",
                };
        }

        // ── SETTINGS ──

        // Files the game and Generals Online write to. A read-only one makes saving fail without a message.
        private static HealthResult CheckReadOnlySettings()
        {
            string data = GamePaths.UserData;
            var candidates = new List<string>();
            foreach (string folder in new[] { data, GamePaths.GoData, Path.Combine(data, "Save") })
            {
                if (Directory.Exists(folder))
                    candidates.AddRange(Directory.EnumerateFiles(folder));
            }
            candidates.Add(Path.Combine(GamePaths.Maps, "MapCache.ini"));
            candidates.Add(Path.Combine(GamePaths.Replays, "00000000.rep")); // the replay the game records into

            var locked = candidates.Where(File.Exists).Where(IsReadOnly).ToList();
            if (locked.Count == 0)
                return new HealthResult { Id = "readonly:settings", Group = GroupSettings, Title = "Settings can be saved", Detail = "Options.ini, Generals Online settings and the last replay are not read-only", Status = HealthStatus.Passed };

            var names = locked.Select(f => Path.GetRelativePath(data, f)).ToList();
            bool replay = locked.Any(f => Path.GetFileName(f).Equals("00000000.rep", StringComparison.OrdinalIgnoreCase));
            return new HealthResult
            {
                Id = "readonly:settings", Group = GroupSettings, Title = "Game settings files are read-only", Status = HealthStatus.Warning,
                Detail = $"{NameList(names)} {(locked.Count == 1 ? "is" : "are")} marked read-only, so the game cannot save to {(locked.Count == 1 ? "it" : "them")}. "
                         + (replay ? "Settings go back after a restart and games are not recorded as replays" : "Settings go back after a restart"),
                Note = "GenPatcher clears this flag too. Some people set it on purpose to keep a setting; the game cannot save any change then.",
                FixLabel = "Fix",
                FixPreview = $"Clears the read-only flag on {NameList(names)}. Their contents stay the same. Undo sets the flag again.",
                Fix = () => BackupService.ClearReadOnly("Cleared read-only settings files", data, locked, string.Join(", ", names)),
            };
        }

        private static HealthResult CheckOptionsFile()
        {
            string path = GamePaths.Options;
            if (!File.Exists(path))
                return new HealthResult { Id = "options", Group = GroupSettings, Title = "Game settings file not created yet", Detail = "The game creates Options.ini with default settings on first start", Status = HealthStatus.Warning };

            // A crash or power cut while the file is written leaves it empty or filled with zero bytes;
            // the game skips what it cannot read and uses default settings
            byte[] content = File.ReadAllBytes(path);
            bool empty = content.All(b => b is 0 or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n');
            bool damaged = !empty && content.Contains((byte)0);
            if (!empty && !damaged)
                return new HealthResult { Id = "options", Group = GroupSettings, Title = "Game settings file", Detail = "Options.ini found", Status = HealthStatus.Passed };

            return new HealthResult
            {
                Id = "options", Group = GroupSettings, Title = empty ? "Game settings file is empty" : "Game settings file is damaged", Status = HealthStatus.Warning,
                Detail = empty
                    ? "Options.ini has no settings in it, so the game starts with the default resolution, graphics and sound"
                    : "Part of Options.ini is unreadable, usually after a crash or power cut. The game skips that part and uses default settings for it",
                FixLabel = "Reset",
                FixPreview = "Moves Options.ini to quarantine; the game writes a fresh one with default settings on next start. Undo puts it back.",
                Fix = () => BackupService.MoveToQuarantine("Reset game settings", path, "Options.ini"),
            };
        }

        private static HealthResult? CheckResolution()
        {
            if (!File.Exists(GamePaths.Options)) return null;
            var options = OptionsFile.Read();
            var (width, height) = Display.Current();
            if (!options.TryGetValue("Resolution", out var value))
                return new HealthResult { Id = "resolution", Group = GroupSettings, Title = "Resolution", Detail = "Not set; the game picks one automatically", Status = HealthStatus.Passed };

            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && Display.Supports(w, h))
                return new HealthResult { Id = "resolution", Group = GroupSettings, Title = "Resolution supported", Detail = $"{w} × {h} matches your display", Status = HealthStatus.Passed };

            return new HealthResult
            {
                Id = "resolution", Group = GroupSettings, Title = "Resolution not supported", Status = HealthStatus.Problem,
                Detail = $"Options.ini is set to {value.Replace(" ", " × ")}, which your display cannot show. Your display is {width} × {height}",
                FixLabel = "Fix",
                FixPreview = $"Sets Resolution in Options.ini to {width} × {height}.",
                Fix = () => OptionsFile.Write(new Dictionary<string, string> { ["Resolution"] = $"{width} {height}" }, "Set resolution", $"Options.ini · {value} → {width} {height}"),
            };
        }

        private static HealthResult? CheckNetworkAddress()
        {
            if (!File.Exists(GamePaths.Options)) return null;
            var options = OptionsFile.Read();
            var local = LocalAddresses();
            if (local.Count == 0) return null;

            var stale = new[] { "IPAddress", "GameSpyIPAddress" }
                .Where(k => options.TryGetValue(k, out var v) && v.Length > 0 && v != "0" && !local.Contains(v))
                .ToList();
            if (stale.Count == 0)
                return new HealthResult { Id = "ip", Group = GroupSettings, Title = "Network address", Detail = "The saved address matches this PC", Status = HealthStatus.Passed };

            string preferred = local[0];
            string old = options[stale[0]];
            return new HealthResult
            {
                Id = "ip", Group = GroupSettings, Title = "Saved network address is old", Status = HealthStatus.Warning,
                Detail = $"Options.ini has {old}, this PC is now {preferred}. LAN games may not find each other",
                FixLabel = "Fix",
                FixPreview = $"Sets {string.Join(" and ", stale)} in Options.ini to {preferred}.",
                Fix = () => OptionsFile.Write(stale.ToDictionary(k => k, _ => preferred), "Updated network address", $"Options.ini · {old} → {preferred}"),
            };
        }

        private static List<string> LocalAddresses()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
        }

        private static HealthResult CheckGoSettings()
        {
            if (!File.Exists(GamePaths.GoSettings))
                return new HealthResult { Id = "gosettings", Group = GroupSettings, Title = "Generals Online settings", Detail = "Not created yet; the game creates settings.json on first start", Status = HealthStatus.Passed };
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(File.ReadAllText(GamePaths.GoSettings));
                return new HealthResult { Id = "gosettings", Group = GroupSettings, Title = "Generals Online settings valid", Detail = "settings.json can be read", Status = HealthStatus.Passed };
            }
            catch (Exception ex)
            {
                return new HealthResult
                {
                    Id = "gosettings", Group = GroupSettings, Title = "Generals Online settings are broken", Status = HealthStatus.Problem,
                    Detail = "settings.json cannot be read and stops the game from starting: " + ex.Message.Split('.')[0],
                    FixLabel = "Reset",
                    FixPreview = "Moves settings.json to quarantine; the game writes a fresh one with default values on next start.",
                    Fix = () => BackupService.MoveToQuarantine("Reset Generals Online settings", GamePaths.GoSettings, "settings.json"),
                };
            }
        }

        private static HealthResult? CheckDocumentsFolder()
        {
            bool cloud = GamePaths.UserData.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
            return cloud ? new HealthResult
            {
                Id = "onedrive", Group = GroupSettings, Title = "Documents folder is in OneDrive", Status = HealthStatus.Warning,
                Detail = "Settings, maps and replays can go missing when OneDrive keeps files online only",
                Note = "In OneDrive, right-click \"Command and Conquer Generals Zero Hour Data\" and choose \"Always keep on this device\".",
            } : null;
        }

        // The game names its Documents folder after UserDataLeafName (current user first, then the machine-wide key
        // that 32-bit programs see). Installers and patchers for other languages sometimes set a different name.
        private static HealthResult? CheckDataFolderName()
        {
            const string zeroHourKey = @"SOFTWARE\Electronic Arts\EA Games\Command and Conquer Generals Zero Hour";
            string? leaf = ReadString(Registry.CurrentUser, zeroHourKey, "UserDataLeafName");
            if (string.IsNullOrWhiteSpace(leaf))
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                leaf = ReadString(machine, zeroHourKey, "UserDataLeafName");
            }
            leaf = leaf?.Trim().TrimEnd('\\');
            string expected = Path.GetFileName(GamePaths.UserData);
            if (string.IsNullOrEmpty(leaf) || leaf.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return null;

            return new HealthResult
            {
                Id = "datafolder", Group = GroupSettings, Title = "The game uses a different Documents folder", Status = HealthStatus.Warning,
                Detail = $"Zero Hour is set to keep settings, maps and replays in Documents\\{leaf}, but Command Center reads Documents\\{expected}. Changes made here may not reach the game",
                Note = "Set by UserDataLeafName in the registry, usually by an installer or patcher for another language.",
            };
        }

        private static string? ReadString(RegistryKey hive, string path, string name)
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }

        // Generals Online opens its files through the classic Windows path limit (259 characters, counted in UTF-8 bytes
        // for letters outside English since the game uses the UTF-8 code page)
        private const int MaxPathLength = 259;
        // Room below the data folder for a downloaded map such as Maps\<name>\<name>.map
        private const int MapPathRoom = 110;

        private static HealthResult CheckDocumentsPath(string game)
        {
            string data = GamePaths.UserData;

            if (!data.All(c => c < 128) && !GameCanUsePath(game, data))
                return new HealthResult
                {
                    Id = "docpath", Group = GroupSettings, Title = "Documents path has letters the game cannot use", Status = HealthStatus.Problem,
                    Detail = "This version of Generals Online cannot open a Documents folder with these letters, so settings, maps and replays are not found",
                    Note = "Update Generals Online, or move the Documents folder to a path with English letters only (right-click Documents › Properties › Location).",
                };

            var tooLong = new List<string>();
            foreach (string folder in new[] { GamePaths.Maps, GamePaths.Replays, Path.Combine(data, "Save") })
            {
                if (Directory.Exists(folder))
                    tooLong.AddRange(Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(f => PathLength(f) > MaxPathLength));
            }
            if (tooLong.Count > 0)
            {
                var names = tooLong.Select(Path.GetFileName).Select(n => n!).ToList();
                string first = Path.GetRelativePath(data, tooLong[0]).Split('\\')[0];
                return new HealthResult
                {
                    Id = "docpath", Group = GroupSettings, Title = "Some maps or replays have paths too long for the game", Status = HealthStatus.Warning,
                    Detail = $"{Plural(tooLong.Count, "file is", "files are")} past the Windows limit of {MaxPathLength} characters, so the game cannot open {(tooLong.Count == 1 ? "it" : "them")}: {NameList(names, 2)}",
                    Note = "Shorten the folder or file names, or move the Documents folder to a shorter path.",
                    GuideLabel = "Show folder",
                    GuideTarget = "select:" + Path.Combine(data, first),
                };
            }

            int length = PathLength(data);
            if (length + 1 + MapPathRoom > MaxPathLength)
                return new HealthResult
                {
                    Id = "docpath", Group = GroupSettings, Title = "Documents path is long", Status = HealthStatus.Warning,
                    Detail = $"The game data folder path is {length} characters long, which leaves {Math.Max(0, MaxPathLength - length - 1)} for maps and replays. Maps with long names will not open",
                    Note = "Moving the Documents folder to a shorter path avoids this (right-click Documents › Properties › Location).",
                };

            return new HealthResult { Id = "docpath", Group = GroupSettings, Title = "Documents path works with the game", Detail = "Short enough for maps and replays, with letters the game can read", Status = HealthStatus.Passed };
        }

        private static int PathLength(string path) => path.All(c => c < 128) ? path.Length : Encoding.UTF8.GetByteCount(path);

        // Mirrors how Generals Online finds the Documents folder: the UTF-8 code page when its manifest asks for it
        // (Windows 10 1903 or newer), otherwise the Windows code page, with the short 8.3 path as a fallback
        private static bool GameCanUsePath(string game, string path)
        {
            if (FitsCodePage(path))
                return true;
            if (!ExeUsesUtf8(Path.Combine(game, GamePaths.GameExe)))
                return false; // older builds had no fallback
            if (Environment.OSVersion.Version.Build >= 18362)
                return true;
            var shortPath = new StringBuilder(MaxPathLength + 2);
            uint length = GetShortPathName(path, shortPath, (uint)shortPath.Capacity);
            return length > 0 && length < shortPath.Capacity && FitsCodePage(shortPath.ToString());
        }

        private static bool ExeUsesUtf8(string exe)
        {
            if (!File.Exists(exe))
                return false;
            byte[] bytes = File.ReadAllBytes(exe);
            int at = bytes.AsSpan().IndexOf("<activeCodePage"u8);
            return at >= 0 && bytes.AsSpan(at, Math.Min(160, bytes.Length - at)).IndexOf("UTF-8"u8) >= 0;
        }

        private static bool FitsCodePage(string text)
        {
            // Windows set to use UTF-8 for every program can hold any letter
            if (GetACP() == 65001)
                return true;
            const uint ansiCodePage = 0, noBestFitChars = 0x400;
            return WideCharToMultiByte(ansiCodePage, noBestFitChars, text, -1, IntPtr.Zero, 0, IntPtr.Zero, out int usedDefault) > 0 && usedDefault == 0;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WideCharToMultiByte(uint codePage, uint flags, string wide, int wideLength, IntPtr multi, int multiLength, IntPtr defaultChar, out int usedDefaultChar);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint length);

        // ── NETWORK ── run separately because they wait on the network

        public static async Task<List<HealthResult>> RunNetworkAsync()
        {
            var results = new List<HealthResult>();
            var stats = await GoApi.GetStatsAsync();
            results.Add(stats != null
                ? new HealthResult { Id = "server", Group = GroupNetwork, Title = "Game server reachable", Detail = $"{stats.LatencyMs} ms to the Generals Online service", Status = HealthStatus.Passed }
                : new HealthResult { Id = "server", Group = GroupNetwork, Title = "Game server not reachable", Detail = "Check your internet connection, VPN or firewall", Status = HealthStatus.Problem });

            try
            {
                results.Add(CheckFirewall());
            }
            catch
            {
                // Firewall rules are optional information
            }
            return results;
        }

        private static HealthResult CheckFirewall()
        {
            string exe = Path.Combine(GamePaths.Game, GamePaths.GameExe);
            Type? type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null)
                throw new InvalidOperationException();
            dynamic policy = Activator.CreateInstance(type)!;
            bool allowed = false, blocked = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app == null || !app.Equals(exe, StringComparison.OrdinalIgnoreCase) || !(bool)rule.Enabled || (int)rule.Direction != 1)
                    continue;
                if ((int)rule.Action == 1) allowed = true;
                else blocked = true;
            }
            Marshal.FinalReleaseComObject(policy);

            if (blocked)
                return new HealthResult
                {
                    Id = "firewall", Group = GroupNetwork, Title = "Windows Firewall blocks the game", Status = HealthStatus.Problem,
                    Detail = "A rule blocks GeneralsOnlineZH_60.exe, so other players cannot connect to you",
                    GuideLabel = "Open firewall", GuideTarget = "firewall",
                };
            return allowed
                ? new HealthResult { Id = "firewall", Group = GroupNetwork, Title = "Windows Firewall allows the game", Detail = "Incoming connections are allowed for GeneralsOnlineZH_60.exe", Status = HealthStatus.Passed }
                : new HealthResult { Id = "firewall", Group = GroupNetwork, Title = "No firewall rule yet", Detail = "Windows asks the first time you host or join; choose Allow", Status = HealthStatus.Passed };
        }

        // ── Display modes ──

        public static class Display
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct DEVMODE
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
                public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
                public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
                public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
                public short dmLogPixels;
                public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
                public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

            private const int CurrentSettings = -1;

            public static (int Width, int Height) Current()
            {
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                return EnumDisplaySettings(null, CurrentSettings, ref mode) ? (mode.dmPelsWidth, mode.dmPelsHeight) : (0, 0);
            }

            public static bool Supports(int width, int height) => Modes().Contains((width, height));

            public static List<(int Width, int Height)> Modes()
            {
                var modes = new List<(int, int)>();
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                for (int i = 0; EnumDisplaySettings(null, i, ref mode); i++)
                {
                    if (mode.dmBitsPerPel >= 32 && !modes.Contains((mode.dmPelsWidth, mode.dmPelsHeight)))
                        modes.Add((mode.dmPelsWidth, mode.dmPelsHeight));
                }
                return modes.OrderBy(m => m.Item1 * m.Item2).ToList();
            }
        }
    }
}
