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

            if (Arg("--library-folder") is { } library)
                AppState.LibraryFolder = library;
            if (Arg("--map-library") is { } mapLibrary)
                MapLibraryClient.SourceOverride = mapLibrary;
            AppState.Samples = HasArg("--samples");

            var window = new MainWindow();
            MainWindow = window;

            if (Arg("--capture") is { } folder)
                _ = window.CaptureAsync(folder, Arg("--size") ?? "1600x1000", Arg("--pages"));
            else
                window.Show();
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
