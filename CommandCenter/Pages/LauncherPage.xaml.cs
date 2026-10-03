using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public partial class LauncherPage : Page
    {
        private bool _initializing = true;
        private bool _playing;
        private bool _loadedOnce;
        private ReplayEntry? _lastMatch;
        private readonly bool _still = App.HasArg("--capture");
        private readonly DispatcherTimer _gameWatch = new() { Interval = TimeSpan.FromSeconds(3) };
        private readonly DispatcherTimer _sheen = new() { Interval = TimeSpan.FromSeconds(5) };

        public LauncherPage()
        {
            InitializeComponent();
            Loaded += LauncherPage_Loaded;
            Loaded += (_, _) => { _gameWatch.Start(); GameWatch_Tick(null, EventArgs.Empty); };
            Unloaded += (_, _) => { _gameWatch.Stop(); _sheen.Stop(); };
            _gameWatch.Tick += GameWatch_Tick;
            _sheen.Tick += (_, _) => Shine();
            AppState.StatsChanged += () => Dispatcher.Invoke(ShowStats);
            AppState.HealthChanged += () => Dispatcher.Invoke(ShowHealth);
            AppState.ReplaysChanged += () => Dispatcher.Invoke(ShowLastMatch);
        }

        private void LauncherPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_still)
                _sheen.Start();
            if (_loadedOnce)
            {
                ShowAll();
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

            ShowAll();
            if (AppState.Stats == null)
                _ = AppState.RefreshStatsAsync();
            if (AppState.HealthCheckedAt == null)
                _ = AppState.RefreshHealthAsync();
            if (AppState.Replays.Count == 0)
                _ = AppState.RefreshReplaysAsync();
        }

        private void ShowAll()
        {
            ShowGame();
            ShowStats();
            ShowHealth();
            ShowLastMatch();
            _ = ShowTilesAsync();
        }

        // The chips under the title: which client, which anti-cheat, which copy of the game
        private void ShowGame()
        {
            VersionText.Text = GamePaths.GameFound
                ? "Generals Online " + AppState.ClientVersion()
                : Loc.T("Generals Online is not installed");

            string? antiCheat = AntiCheatName();
            AntiCheatChip.Visibility = antiCheat == null ? Visibility.Collapsed : Visibility.Visible;
            AntiCheatText.Text = antiCheat ?? "";

            if (!GamePaths.ZeroHourFound)
                SourceText.Text = Loc.T("Game not found");
            else
                SourceText.Text = GamePaths.Game.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)
                    ? Loc.T("Steam copy")
                    : Loc.T("Other copy");
            SourceChip.ToolTip = GamePaths.ZeroHourFound ? GamePaths.Game : null;
        }

        private void ShowStats()
        {
            var stats = AppState.Stats;
            if (stats == null)
            {
                PlayersText.Text = "—";
                LobbiesText.Text = "—";
                PingText.Text = "—";
                ServerState.Text = Loc.T("Checking…");
                ServerState.Foreground = Views.Hint;
                ServerDot.Fill = Views.Hint;
                return;
            }
            Count(PlayersText, stats.Players);
            LobbiesText.Text = stats.Lobbies.ToString("N0", Loc.Culture);
            PingText.Text = stats.LatencyMs > 0 ? stats.LatencyMs + " ms" : "—";
            ServerState.Text = Loc.T("Online");
            ServerState.Foreground = Views.Brush("#7BE39A");
            ServerDot.Fill = Views.Brush("#52D273");
        }

        // Counts up to the number the first time it shows
        private void Count(TextBlock box, int to)
        {
            int.TryParse(box.Text, System.Globalization.NumberStyles.AllowThousands, Loc.Culture, out int from);
            if (_still || from == to)
            {
                box.Text = to.ToString("N0", Loc.Culture);
                return;
            }
            var start = DateTime.Now;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 900);
                double eased = 1 - Math.Pow(1 - t, 3);
                box.Text = ((int)Math.Round(from + (to - from) * eased)).ToString("N0", Loc.Culture);
                if (t >= 1)
                    timer.Stop();
            };
            timer.Start();
        }

        // A coloured line under PLAY: problems in red, warnings in amber, or a quiet all-clear
        private void ShowHealth()
        {
            if (AppState.HealthCheckedAt == null)
            {
                HealthChip.Visibility = Visibility.Collapsed;
                return;
            }
            int problems = AppState.ProblemCount;
            int warnings = AppState.WarningCount;
            string color;
            if (problems + warnings == 0)
            {
                color = "#52D273";
                HealthIcon.Kind = "check-circle";
                HealthText.Text = Loc.T("Your game passed every check");
                HealthReview.Visibility = Visibility.Collapsed;
            }
            else
            {
                color = problems > 0 ? "#FF5A5F" : "#FFAA00";
                var parts = new List<string>();
                if (problems > 0) parts.Add(Loc.N(problems, "{0} problem", "{0} problems"));
                if (warnings > 0) parts.Add(Loc.N(warnings, "{0} warning", "{0} warnings"));
                HealthIcon.Kind = "alert";
                HealthText.Text = Loc.T("{0} found with your game", Loc.List(parts));
                HealthReview.Visibility = Visibility.Visible;
            }
            var brush = Views.Brush(color);
            HealthIcon.Foreground = brush;
            HealthText.Foreground = Views.Brush("#E6E9F5");
            HealthReview.Foreground = brush;
            HealthChip.Background = new SolidColorBrush(Color.FromArgb(0x1F, brush.Color.R, brush.Color.G, brush.Color.B));
            HealthChip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, brush.Color.R, brush.Color.G, brush.Color.B));
            HealthChip.Visibility = Visibility.Visible;
        }

        // The newest replay the game recorded, with its map and the recorder's result when it is known
        private void ShowLastMatch()
        {
            _lastMatch = AppState.Replays.OrderByDescending(r => r.Start).FirstOrDefault();
            if (_lastMatch == null)
            {
                LastMatchCard.Visibility = Visibility.Collapsed;
                ReplaysTileText.Text = Loc.T("No replays yet");
                return;
            }
            ReplaysTileText.Text = Loc.N(AppState.Replays.Count, "{0} replay", "{0} replays");

            var match = _lastMatch;
            string map = Tools.ReplayRow.MapName(match, MapCatalog.InstalledFolders());
            LastMatchName.Text = map.Length > 0 ? map : Loc.T("Unknown map");
            LastMatchInfo.Text = Loc.Join(new[] { Views.Day(match.Start), Loc.Ltr(Views.Length(match.Length)), Tools.ReplayRow.ModeText(match.Mode) });
            LastMatchMap.ImageSource = Views.MapPreview(match.MapPath, match.Map);
            LastResultChip.Visibility = Visibility.Collapsed;
            LastMatchCard.Visibility = Visibility.Visible;

            _ = Task.Run(() => ReplayService.ReadResults(match)).ContinueWith(task =>
            {
                if (task.IsFaulted || task.Result is not { } results || _lastMatch != match)
                    return;
                var result = results.Recorder >= 0 && results.Recorder < results.Players.Count ? results.Players[results.Recorder].Result : null;
                if (result == null)
                    return;
                bool won = result == MatchResult.Won;
                var brush = Views.Brush(won ? "#52D273" : "#FF5A5F");
                LastResult.Text = won ? Loc.T("WON") : Loc.T("LOST");
                LastResult.Foreground = brush;
                LastResultChip.Background = new SolidColorBrush(Color.FromArgb(0x26, brush.Color.R, brush.Color.G, brush.Color.B));
                LastResultChip.Visibility = Visibility.Visible;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private async Task ShowTilesAsync()
        {
            var (kind, resolution) = AddonService.Installed();
            BarTileText.Text = kind switch
            {
                BarKind.Pro => "Control Bar Pro" + (resolution != null ? " " + resolution : ""),
                BarKind.Other => Loc.T("Another control bar"),
                _ => Loc.T("Original control bar"),
            };
            // The Pro bar's English name keeps its reading order and is shortened at its own end
            if (kind == BarKind.Pro)
            {
                BarTileText.FlowDirection = FlowDirection.LeftToRight;
                BarTileText.TextAlignment = Loc.StartAlignment;
            }
            else
            {
                BarTileText.ClearValue(FlowDirectionProperty);
                BarTileText.ClearValue(TextBlock.TextAlignmentProperty);
            }

            int maps = await Task.Run(() =>
            {
                try
                {
                    return Directory.Exists(GamePaths.Maps)
                        ? Directory.EnumerateDirectories(GamePaths.Maps).Count(d => Directory.EnumerateFiles(d, "*.map").Any())
                        : 0;
                }
                catch
                {
                    return 0;
                }
            });
            MapsTileText.Text = maps == 0 ? Loc.T("Browse and install maps") : Loc.N(maps, "{0} map installed", "{0} maps installed");
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

        private void HealthChip_Click(object sender, RoutedEventArgs e) => Views.Main.ShowTools("health");

        private void SourceChip_Click(object sender, RoutedEventArgs e) => Views.Main.ShowOptions("game");

        private void Tile_Click(object sender, RoutedEventArgs e) => Views.Main.ShowTools(((FrameworkElement)sender).Tag as string);

        private void WatchLast_Click(object sender, RoutedEventArgs e)
        {
            if (_lastMatch == null)
                return;
            try
            {
                ReplayService.Watch(_lastMatch);
            }
            catch (Exception ex)
            {
                Views.Main.Toast(ex.Message, isError: true);
            }
        }

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
                ShowGameState(true, starting: true);
                var channel = ClientSelectorPanel.Visibility == Visibility.Visible && rbTestEnv.IsChecked == true ? Channel.Test : Channel.Live;
                GameLauncher.PlayOnline(channel);

                // Stays open under the game while it starts (it hands the focus back to the game if Windows gives it
                // here), then steps aside without taking the focus; back when the game closes
                var handle = new WindowInteropHelper(window).Handle;
                await GameLauncher.WaitForGameAsync(handle, () =>
                {
                    minimized = true;
                    Dispatcher.Invoke(() => ShowGameState(true));
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
            if (answer != MessageBoxResult.Yes || !Views.ChooseGameFolder(Window.GetWindow(this)))
                return false;
            ShowGame();
            return true;
        }

        // PLAY waits while the game runs, whoever started it (the official launcher does the same)
        private void ShowGameState(bool running, bool starting = false)
        {
            PlayButton.IsEnabled = !running;
            PlayButton.Opacity = running ? 0.85 : 1;
            PlayIdle.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            PlayBusy.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            PlayBusyText.Text = starting ? Loc.T("Starting…") : Loc.T("RUNNING");

            var spin = (RotateTransform)Spinner.RenderTransform;
            if (running && !_still)
                spin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            else
                spin.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        // A light sweep across PLAY every few seconds while it waits
        private void Shine()
        {
            if (!PlayButton.IsEnabled || !IsVisible)
                return;
            SheenMove.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-90, PlayButton.ActualWidth + 40, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }

        private async void GameWatch_Tick(object? sender, EventArgs e)
        {
            if (_playing || !IsVisible)
                return;
            bool running = await Task.Run(GameLauncher.IsGameRunning);
            if (!_playing && running != !PlayButton.IsEnabled)
                ShowGameState(running);
        }

        private const int MinimizedNoActivate = 7;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
    }
}
