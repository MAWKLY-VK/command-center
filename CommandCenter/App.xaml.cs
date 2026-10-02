using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter
{
    public partial class App : Application
    {
        public static string? Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        public static bool HasArg(string name) => Environment.GetCommandLineArgs().Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandled;
            UpdateService.CleanUp();

            // --lang en|ar picks the language for this run only, without changing the saved setting
            Loc.Init(Loc.Normalize(Arg("--lang")) ?? AppSettings.Current.Language);
            if (Loc.Language != "en")
            {
                CultureInfo.DefaultThreadCurrentCulture = Loc.Culture;
                CultureInfo.CurrentCulture = Loc.Culture;
            }

            if (Arg("--library-folder") is { } library)
                AppState.LibraryFolder = library;
            if (Arg("--map-library") is { } mapLibrary)
                MapLibraryClient.SourceOverride = mapLibrary;
            AppState.Samples = HasArg("--samples");

            var window = new MainWindow { FlowDirection = Loc.FlowDirection };
            MainWindow = window;

            if (Arg("--capture") is { } folder)
                _ = window.CaptureAsync(folder, Arg("--size") ?? "1600x1000", Arg("--pages"));
            else
                window.Show();
        }

        // Next to --capture screenshots, list the wrapped strings that still have no translation
        protected override void OnExit(ExitEventArgs e)
        {
            if (Arg("--capture") is { } folder && Loc.Language != "en")
            {
                try
                {
                    File.WriteAllLines(Path.Combine(folder, $"missing-{Loc.Language}.txt"), Loc.Missing);
                }
                catch { }
            }
            base.OnExit(e);
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            if (MainWindow is MainWindow main && main.IsVisible)
                main.Toast("Something went wrong: " + e.Exception.Message, isError: true);
            else
                MessageBox.Show(e.Exception.ToString(), "Command Center", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
