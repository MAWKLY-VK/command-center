using System.IO;

namespace CommandCenter
{
    // Data several pages share, loaded once in the background and refreshed on demand.
    public static class AppState
    {
        public static ServerStats? Stats { get; private set; }
        public static event Action? StatsChanged;

        // An answer was asked for at least once, so a missing one means the service could not be reached
        public static bool StatsChecked { get; private set; }

        public static List<HealthResult> Health { get; private set; } = new();
        public static DateTime? HealthCheckedAt { get; private set; }
        public static event Action? HealthChanged;

        public static List<ReplayEntry> Replays { get; private set; } = new();
        public static event Action? ReplaysChanged;

        private static Task<HotkeyService?>? _hotkeys;
        private static Task _readHotkeysAfter = Task.CompletedTask;

        public static int ProblemCount => Health.Count(h => h.Status == HealthStatus.Problem);
        public static int WarningCount => Health.Count(h => h.Status == HealthStatus.Warning);

        public static async Task RefreshStatsAsync()
        {
            Stats = await GoApi.GetStatsAsync();
            StatsChecked = true;
            StatsChanged?.Invoke();
        }

        // --samples adds made-up problems so the problem layouts can be reviewed on a healthy PC; their fixes do nothing
        public static bool Samples { get; set; }

        public static async Task RefreshHealthAsync()
        {
            var local = await Task.Run(HealthService.Run);
            var network = await HealthService.RunNetworkAsync();
            Health = local.Concat(network).ToList();
            if (Samples)
                Health.InsertRange(0, SampleIssues());
            HealthCheckedAt = DateTime.Now;
            HealthChanged?.Invoke();
        }

        public static async Task RefreshReplaysAsync()
        {
            Replays = await Task.Run(ReplayService.ScanAll);
            ReplaysChanged?.Invoke();
        }

        public static Task<HotkeyService?> Hotkeys()
        {
            _hotkeys ??= Task.Run<HotkeyService?>(async () =>
            {
                // The step logs its own errors; the hotkeys are read either way
                try { await _readHotkeysAfter; }
                catch { }
                try { return HotkeyService.Load(GamePaths.Game); }
                catch { return null; }
            });
            return _hotkeys;
        }

        // The first start's one-time step can change the game's text table, so the hotkeys are read after it
        public static void ReadHotkeysAfter(Task step) => _readHotkeysAfter = step;

        // The hotkeys are read again next time: the game folder, the community data patch or the game's text changed
        public static void ForgetHotkeys() => _hotkeys = null;

        // Written like the texts of the real checks
        private static IEnumerable<HealthResult> SampleIssues()
        {
            static string Sample(string note) => note + " (Sample)";
            string exe = GamePaths.GameExe;
            yield return new HealthResult
            {
                Id = "sample:runtime", Group = HealthService.GroupFiles, Title = "Old Visual C++ file inside the game folder", Status = HealthStatus.Problem,
                Detail = "msvcp140.dll (14.0.24215.1) is loaded instead of the system copy, so the game stops with \"Entry Point Not Found\"",
                Note = Sample("Usually left by GenPatcher or an old repack."),
                FixLabel = "Quarantine", FixPreview = "Moves msvcp140.dll to the launcher's quarantine folder. Undo puts it back.", Fix = () => { },
            };
            yield return new HealthResult
            {
                Id = "sample:archive", Group = HealthService.GroupFiles, Title = "ExtraRulesZH.big changes unit rules", Status = HealthStatus.Problem,
                Detail = "Other players do not have it, so online games end with \"The host has modified INI files\"",
                FixLabel = "Quarantine", FixPreview = "Moves ExtraRulesZH.big to the launcher's quarantine folder. Undo puts it back.", Fix = () => { },
            };
            yield return new HealthResult
            {
                Id = "sample:admin", Group = HealthService.GroupWindows, Title = "\"Run as administrator\" is set on the game", Status = HealthStatus.Warning,
                Detail = "Windows blocks the game with error 740 \"The requested operation requires elevation\"",
                Note = Sample(string.Format("Flag RUNASADMIN on {0}", exe)),
                FixLabel = "Fix", FixPreview = string.Format("Removes RUNASADMIN from the compatibility settings of {0}. Other settings stay.", exe), Fix = () => { },
            };
        }

        public static string Version => typeof(AppState).Assembly.GetName().Version?.ToString(2) ?? "0.1";

        public static string ClientVersion()
        {
            try
            {
                // Generals Online keeps the last update it installed as GeneralsOnline_update_<version>.exe
                string folder = Path.Combine(GamePaths.GoData, "Update");
                var update = Directory.Exists(folder)
                    ? new DirectoryInfo(folder).EnumerateFiles("GeneralsOnline_update_*.exe").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
                    : null;
                if (update != null)
                    return Path.GetFileNameWithoutExtension(update.Name)["GeneralsOnline_update_".Length..];
                return File.GetLastWriteTime(Path.Combine(GamePaths.Game, GamePaths.GameExe)).ToString("MMddyy");
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
