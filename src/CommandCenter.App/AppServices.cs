using System.Windows;

namespace CommandCenter
{
    // The hand-written composition root (ADR 0004): the one place where the services get what they need from the App at
    // startup, which is the run options. The services stay static until their pages move to MVVM in phases 5 and 6;
    // from then on, the ViewModels get them from here.
    internal static class AppServices
    {
        // The instance forms the view models use (ticket 08); pages that have not moved keep the static services
        public static IGameFolder GameFolder { get; } = new CommandCenter.Core.Game.GameFolder();
        public static IAppearance Appearance { get; } = new CommandCenter.Core.Settings.Appearance(() => SystemParameters.ClientAreaAnimation);
        public static IAddons Addons { get; } = new CommandCenter.Core.Content.Addons();

        // Health (ticket 10): the one owner of the checks' results, the fixes, and the history of changes. A fix or an
        // Undo can change the game's text table, which the hotkey editor reads, so both forget the hotkeys after.
        public static IHealthChecks Health { get; } = new HealthChecks(RunHealthChecksAsync, () => DateTime.Now);
        public static FixRunner Fixes { get; } = new(AppState.ForgetHotkeys);
        public static FixHistory History { get; } = new(AppState.ForgetHotkeys);

        // Every check: the local ones off the UI thread, then the network ones; --samples puts its made-up problems first
        private static async Task<IReadOnlyList<HealthResult>> RunHealthChecksAsync()
        {
            var local = await Task.Run(HealthService.Run);
            var network = await HealthService.RunNetworkAsync();
            var all = local.Concat(network).ToList();
            if (AppState.Samples)
                all.InsertRange(0, AppState.SampleIssues());
            return all;
        }

        // First thing at startup, before anything reads a service. args are the command line as the program got it.
        public static void Configure(IReadOnlyList<string> args)
        {
            GoProfile.FileOverride = Value(args, "--go-profile");
            MapLibraryClient.SourceOverride = Value(args, "--map-library");
            MapLibraryClient.LibraryFolder = Value(args, "--library-folder");
            AppState.Samples = Has(args, "--samples");

            // Another game folder: its hotkeys are read again, and the checks and the match guard run for it (Home's
            // picture follows in MainWindow)
            GameFolder.Changed += () =>
            {
                AppState.ForgetHotkeys();
                _ = AppState.RefreshHealthAsync();
                _ = AppState.RefreshMatchGuardAsync();
            };
#if TEST_KEYS
            // Test builds only (built with -p:TestKeys=true and never published): the signed files may also come from the
            // test key in this file, so the update path can be tried without the maintainer's key
            if (Value(args, "--test-key") is { } testKey)
            {
                ManifestVerifier.TestKey = ManifestVerifier.KeyFromPem(System.IO.File.ReadAllText(testKey));
                UpdateService.AllowLocalDownloads = true;
            }
#endif
        }

        // The value after an option, as App.Arg reads it
        private static string? Value(IReadOnlyList<string> args, string name)
        {
            int index = Index(args, name);
            return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
        }

        private static bool Has(IReadOnlyList<string> args, string name) => Index(args, name) >= 0;

        private static int Index(IReadOnlyList<string> args, string name)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }
    }
}
