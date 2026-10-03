using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public partial class LauncherPage : Page
    {
        private bool _initializing = true;
        private bool _playing;
        private readonly DispatcherTimer _gameWatch = new() { Interval = TimeSpan.FromSeconds(3) };

        public LauncherPage()
        {
            InitializeComponent();
            Loaded += LauncherPage_Loaded;
            Loaded += (_, _) => { _gameWatch.Start(); GameWatch_Tick(null, EventArgs.Empty); };
            Unloaded += (_, _) => _gameWatch.Stop();
            _gameWatch.Tick += GameWatch_Tick;
            AppState.StatsChanged += () => Dispatcher.Invoke(ShowStats);
            AppState.HealthChanged += () => Dispatcher.Invoke(ShowHealthNotice);
        }

        private bool _loadedOnce;

        private void LauncherPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce)
            {
                ShowStats();
                ShowHealthNotice();
                return;
            }
            _loadedOnce = true;

            if (GameLauncher.HasTestClient)
                ClientSelectorPanel.Visibility = Visibility.Visible;

            try { Directory.CreateDirectory(GamePaths.GoData); } catch { }

            var launcher = LauncherJson.Load();
            rbLiveClient.IsChecked = launcher.PreferLive;
            rbTestEnv.IsChecked = !launcher.PreferLive;
            DefaultAnticheatToFirstPlugin();
            _initializing = false;

            ShowStats();
            ShowHealthNotice();
            if (AppState.Stats == null)
                _ = AppState.RefreshStatsAsync();
            if (AppState.HealthCheckedAt == null)
                _ = AppState.RefreshHealthAsync();
        }

        private void ShowStats()
        {
            var stats = AppState.Stats;
            if (stats == null)
            {
                StatsLabel.Text = "";
                return;
            }
            var parts = new List<string>
            {
                Loc.N(stats.Players, "{0} Player Online", "{0} Players Online"),
                Loc.N(stats.Lobbies, "{0} Lobby", "{0} Lobbies"),
            };
            if (AntiCheatName() is { } name)
                parts.Add(Loc.T("Anti-cheat: {0}", name));
            StatsLabel.Text = string.Join("  •  ", parts);
        }

        // Shows a line only when the health checks found something that needs attention
        private void ShowHealthNotice()
        {
            int problems = AppState.ProblemCount;
            int warnings = AppState.WarningCount;
            if (problems + warnings == 0)
            {
                HealthNotice.Visibility = Visibility.Collapsed;
                return;
            }
            var parts = new List<string>();
            if (problems > 0) parts.Add(Loc.N(problems, "{0} problem", "{0} problems"));
            if (warnings > 0) parts.Add(Loc.N(warnings, "{0} warning", "{0} warnings"));
            HealthNoticeText.Text = "⚠  " + Loc.T("{0} found with your game", Loc.List(parts)) + "  •  ";
            HealthNotice.Visibility = Visibility.Visible;
        }

        // The display name of the anti-cheat plugin selected in settings.json, read from the plugin's own JSON file
        private static string? AntiCheatName()
        {
            try
            {
                string id = GoSettings.Load().AntiCheat;
                if (string.IsNullOrEmpty(id))
                    return null;
                string folder = Path.Combine(GamePaths.Game, "plugins", id);
                string? json = Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*.json").OrderBy(Path.GetFileName).FirstOrDefault()
                    : null;
                if (json == null)
                    return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                return doc.RootElement.TryGetProperty("plugin_name", out var name) ? name.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        // Same as the official launcher: when the selected anti-cheat plugin is gone, pick the first installed one
        private static void DefaultAnticheatToFirstPlugin()
        {
            try
            {
                if (!File.Exists(GamePaths.GoSettings))
                    return;
                var settings = GoSettings.Load();
                if (settings.Root["plugins"] == null)
                    return;
                string plugins = Path.Combine(GamePaths.Game, "plugins");
                if (!Directory.Exists(plugins))
                    return;

                string current = settings.AntiCheat;
                if (!string.IsNullOrEmpty(current))
                {
                    string folder = Path.Combine(plugins, current);
                    if (Directory.Exists(folder) && Directory.GetFiles(folder, "*.dll").Length > 0)
                        return;
                }

                var first = Directory.GetDirectories(plugins)
                    .Where(dir => Directory.GetFiles(dir, "*.json").Length > 0 && Directory.GetFiles(dir, "*.dll").Length > 0)
                    .OrderBy(dir => new DirectoryInfo(dir).Name)
                    .FirstOrDefault();
                if (first != null)
                {
                    settings.Set("plugins", "anticheat", new DirectoryInfo(first).Name);
                    settings.Save("Selected the installed anti-cheat plugin");
                }
            }
            catch
            {
                // Generals Online repairs its own settings on start
            }
        }

        private void ClientSelector_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing)
                return;
            try
            {
                var launcher = LauncherJson.Load();
                launcher.PreferLive = rbLiveClient.IsChecked == true;
                launcher.Save();
            }
            catch { }
        }

        private void BtnOptions_Click(object sender, RoutedEventArgs e) => Views.Main.ShowOptions();

        private void BtnTools_Click(object sender, RoutedEventArgs e) => Views.Main.ShowTools(null);

        private void HealthNotice_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Views.Main.ShowTools("health");

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_playing)
                return;
            if (!GamePaths.ZeroHourFound && !LocateGame())
                return;
            if (!GamePaths.GameFound)
            {
                var answer = MessageBox.Show(Loc.T("Generals Online is not installed in the game folder:\n{0}\n\nInstall it, then press Play again. Open the Generals Online download page?", GamePaths.Game),
                    "Command Center", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes, Loc.MessageBoxOptions);
                if (answer == MessageBoxResult.Yes)
                    Views.Open(HealthService.DownloadPage);
                return;
            }
            if (GameLauncher.IsGameRunning())
            {
                ShowGameState(true);
                MessageBox.Show(Loc.T("The game is already running."), "Command Center",
                    MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, Loc.MessageBoxOptions);
                return;
            }

            var window = Application.Current.MainWindow;
            bool minimized = false;
            try
            {
                _playing = true;
                ShowGameState(true);
                var channel = ClientSelectorPanel.Visibility == Visibility.Visible && rbTestEnv.IsChecked == true ? Channel.Test : Channel.Live;
                GameLauncher.PlayOnline(channel);

                // Stays open under the game while it starts (it hands the focus back to the game if Windows gives it
                // here), then steps aside without taking the focus; back when the game closes
                var handle = new WindowInteropHelper(window).Handle;
                await GameLauncher.WaitForGameAsync(handle, () =>
                {
                    minimized = true;
                    ShowWindow(handle, MinimizedNoActivate);
                });
                if (minimized)
                {
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                }
                _ = AppState.RefreshReplaysAsync();
            }
            catch (Exception ex)
            {
                if (minimized)
                    window.WindowState = WindowState.Normal;
                MessageBox.Show(Loc.T("Failed to launch the game: {0}", ex.Message), "Command Center",
                    MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, Loc.MessageBoxOptions);
            }
            finally
            {
                _playing = false;
                ShowGameState(false);
            }
        }

        // Zero Hour was not found by itself: the player picks its folder, which is remembered
        private bool LocateGame()
        {
            var answer = MessageBox.Show(Loc.T("Command & Conquer Generals - Zero Hour was not found. Choose its folder now?"), "Command Center",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes, Loc.MessageBoxOptions);
            if (answer != MessageBoxResult.Yes)
                return false;
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Choose the Zero Hour folder") };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return false;
            if (!GamePaths.UseGameFolder(dialog.FolderName))
            {
                MessageBox.Show(Loc.T("That folder does not hold Zero Hour. Choose the folder with WindowZH.big and INIZH.big in it."), "Command Center",
                    MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Loc.MessageBoxOptions);
                return false;
            }
            AppState.ForgetHotkeys();
            _ = AppState.RefreshHealthAsync();
            return true;
        }

        // PLAY waits while the game runs, whoever started it (the official launcher does the same)
        private void ShowGameState(bool running)
        {
            PlayButton.IsEnabled = !running;
            PlayButton.Content = running ? Loc.T("RUNNING") : Loc.T("PLAY");
        }

        private async void GameWatch_Tick(object? sender, EventArgs e)
        {
            if (_playing || !IsVisible)
                return;
            bool running = await Task.Run(GameLauncher.IsGameRunning);
            if (!_playing)
                ShowGameState(running);
        }

        private const int MinimizedNoActivate = 7;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        private void BtnExit_Click(object sender, RoutedEventArgs e) => Application.Current.MainWindow.Close();
    }
}
