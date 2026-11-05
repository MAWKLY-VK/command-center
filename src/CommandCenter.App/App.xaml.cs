using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommandCenter.Localization;

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

        // One Command Center per data folder (decision D2); null when this copy closed because another one runs
        private SingleInstance? _instance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // A second copy for the same data folder brings the first one to the front and closes quietly
            string[] commandLine = Environment.GetCommandLineArgs();
            try
            {
                _instance = SingleInstance.Acquire(GamePaths.UserData, SingleInstance.RestartOf(commandLine));
            }
            catch (Exception ex)
            {
                // For example a copy started as administrator holds the names: run without the lock rather than not at all
                LogError(ex);
                _instance = null;
            }
            if (_instance is { IsFirst: false })
            {
                _instance.AskFirstToShow();
                _instance.Dispose();
                _instance = null;
                Shutdown();
                return;
            }
            _instance?.OnShowRequested(() => Dispatcher.BeginInvoke(ShowWindowAgain));

            DispatcherUnhandledException += OnUnhandled;
            // A background task that fails with nobody waiting for it (a refresh, a download) would vanish without a trace
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                args.SetObserved();
                LogError(args.Exception);
            };
            // The run options reach the services before anything reads them
            AppServices.Configure(commandLine);
            UpdateService.CleanUp();

            // Generals Online's current program names and service address, in the background
            _ = GoProfile.RefreshAsync();

            // --lang en|ar picks the language for this run only, without changing the saved setting
            Loc.Init(Loc.Normalize(Arg("--lang")) ?? AppSettings.Current.Language);
            if (Loc.Language != "en")
            {
                CultureInfo.DefaultThreadCurrentCulture = Loc.Culture;
                CultureInfo.CurrentCulture = Loc.Culture;
            }

            var window = new MainWindow { FlowDirection = LocUi.FlowDirection };
            MainWindow = window;

            if (Arg("--capture") is { } folder)
                _ = window.CaptureAsync(folder, Arg("--size") ?? "1600x1000", Arg("--pages"));
            else
                window.Show();
        }

        // Another copy was started for this data folder: show this one instead, restored if it was minimized
        private void ShowWindowAgain()
        {
            if (MainWindow is not { IsLoaded: true } window)
                return;
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }

        // Next to --capture screenshots, list the wrapped strings that still have no translation
        protected override void OnExit(ExitEventArgs e)
        {
            // On the UI thread, which took the lock in OnStartup
            _instance?.Dispose();

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

        // errors.log in Command Center's folder, to attach to a bug report; it starts over past 256 KB
        public static void LogError(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(GamePaths.AppData);
                string path = Path.Combine(GamePaths.AppData, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {AppState.Version}  {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            LogError(e.Exception);
            if (MainWindow is MainWindow main && main.IsVisible)
                main.Toast(Loc.T("Something went wrong: {0}", e.Exception.Message), isError: true);
            else
                MessageBox.Show(e.Exception.ToString(), "Command Center", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
