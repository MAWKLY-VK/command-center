using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommandCenter.Controls;
using CommandCenter.Pages;
using CommandCenter.Services;

namespace CommandCenter
{
    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, IPage> _pages = new();
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(7) };
        private Action? _toastAction;
        private string _current = "";

        public MainWindow()
        {
            InitializeComponent();
            FitToScreen();
            LoadArt();

            _toastTimer.Tick += (_, _) => HideToast();
            AppState.StatsChanged += ShowStats;
            AppState.HealthChanged += ShowHealthBadge;
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;

            ClientVersion.Text = "Client " + AppState.ClientVersion();
            AppVersion.Text = "Launcher " + AppState.Version + " test";

            Navigate("home");
            _ = AppState.RefreshStatsAsync();
            _ = AppState.RefreshHealthAsync();
            _ = AppState.RefreshReplaysAsync();

            // Screenshots skip the update check unless a manifest is given
            string? updateSource = App.Arg("--update-url");
            if (updateSource != null || !App.HasArg("--capture"))
                _updateCheck = CheckUpdateAsync(updateSource ?? UpdateService.ManifestUrl);
        }

        // Opens at 1440 x 900 or 92% of the work area, whichever is smaller
        private void FitToScreen()
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(1440, area.Width * 0.92));
            Height = Math.Max(MinHeight, Math.Min(900, area.Height * 0.92));
        }

        private void LoadArt()
        {
            if (GamePaths.SteamArt("library_hero.jpg") is { } hero)
                Hero.Source = LoadBitmap(hero);
            if (GamePaths.SteamArt("logo.png") is { } logo)
                Logo.Source = LoadBitmap(logo);
            else
            {
                Logo.Visibility = Visibility.Collapsed;
                LogoFallback.Visibility = Visibility.Visible;
            }
        }

        public static BitmapImage? LoadBitmap(string path, int decodeWidth = 0)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                if (decodeWidth > 0)
                    image.DecodePixelWidth = decodeWidth;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        // ── Navigation ──

        private IPage CreatePage(string id) => id switch
        {
            "maps" => new MapsPage(),
            "replays" => new ReplaysPage(),
            "hotkeys" => new HotkeysPage(),
            "health" => new HealthPage(),
            "addons" => new AddonsPage(),
            "settings" => new SettingsPage(),
            _ => new HomePage(),
        };

        public void Navigate(string id)
        {
            if (!_pages.TryGetValue(id, out var page))
            {
                page = CreatePage(id);
                _pages[id] = page;
            }

            _current = id;
            var nav = id switch
            {
                "maps" => NavMaps, "replays" => NavReplays, "hotkeys" => NavHotkeys, "health" => NavHealth,
                "addons" => NavAddons, "settings" => NavSettings, _ => NavHome,
            };
            if (nav.IsChecked != true)
                nav.IsChecked = true;

            TitleText.Text = page.Title;
            CrumbText.Text = "/ " + page.Crumb;
            TitleIcon.Data = (Geometry)FindResource(page.IconKey);
            TopRight.Content = page.TopRight;
            PageHost.Content = page;
            ShadeHeavy.Opacity = id == "home" ? 0 : 1;

            // Home shows Play and the server numbers in full, so the sidebar copies stay hidden there
            SideFooter.Visibility = id == "home" ? Visibility.Collapsed : Visibility.Visible;
            RefreshFooter();
            page.OnShown();

            var fade = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(140));
            ((UIElement)page).BeginAnimation(OpacityProperty, fade);
        }

        public void RefreshHeader()
        {
            if (_pages.TryGetValue(_current, out var page))
                CrumbText.Text = "/ " + page.Crumb;
        }

        public void RefreshFooter()
        {
            if (!_pages.TryGetValue(_current, out var page))
                return;
            FooterHost.Content = page.Footer;
            FooterBar.Visibility = page.Footer is { Visibility: Visibility.Visible } ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string id } && id != _current && IsInitialized && PageHost != null)
                Navigate(id);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (UpdateOverlay.IsOpen)
            {
                UpdateOverlay.OnKey(e);
                return;
            }
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && _pages.TryGetValue(_current, out var page))
            {
                page.FocusSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SidePlay_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (UpdateOverlay.Restarting)
                return;
            var pending = _pages.Values.Where(p => p.HasPendingChanges).Select(p => p.Title).ToList();
            if (pending.Count == 0)
                return;
            var answer = MessageBox.Show(this, $"You have unsaved changes in {string.Join(" and ", pending)}. Close without saving?",
                "Command Center", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                e.Cancel = true;
        }

        // ── Server and health status ──

        private void ShowStats()
        {
            var stats = AppState.Stats;
            if (stats == null)
            {
                ServerMark.Mark = Mark.Danger;
                ServerState.Text = "Offline";
                ServerPlayers.Text = ServerLobbies.Text = "—";
                return;
            }
            ServerMark.Mark = Mark.Ok;
            ServerState.Text = "Online";
            ServerPlayers.Text = stats.Players.ToString("N0");
            ServerLobbies.Text = stats.Lobbies.ToString("N0");
        }

        private void ShowHealthBadge()
        {
            int problems = AppState.ProblemCount, warnings = AppState.WarningCount;
            Ui.SetBadge(NavHealth, problems + warnings > 0 ? (problems + warnings).ToString() : null);
            Ui.SetBadgeMark(NavHealth, problems > 0 ? Mark.Danger : Mark.Warn);
        }

        // ── Play ──

        public Channel Channel { get; set; } = LauncherJson.Load().PreferLive ? Channel.Live : Channel.Test;

        private void SidePlay_Click(object sender, RoutedEventArgs e) => _ = PlayAsync();

        public async Task PlayAsync()
        {
            if (!GamePaths.GameFound)
            {
                Toast("Generals Online was not found. Start Command Center from the game folder or pass --game <folder>.", isError: true);
                return;
            }
            if (GameLauncher.IsGameRunning())
            {
                Toast("The game is already running.", isError: true);
                return;
            }
            try
            {
                GameLauncher.PlayOnline(Channel);
            }
            catch (Exception ex)
            {
                Toast("Could not start the game: " + ex.Message, isError: true);
                return;
            }

            WindowState = WindowState.Minimized;
            await GameLauncher.WaitForGameAsync();
            WindowState = WindowState.Normal;
            Activate();
            _ = AppState.RefreshReplaysAsync();
            _ = AppState.RefreshStatsAsync();
        }

        // ── Mandatory update ──

        private Task? _updateCheck;

        private async Task CheckUpdateAsync(string source)
        {
            if (await UpdateService.CheckAsync(source) is not { } update)
                return;
            ToastBox.Visibility = Visibility.Collapsed;
            UpdateOverlay.Open(update);
            // Test switch: install straight away without a click
            if (App.HasArg("--update-auto"))
                await UpdateOverlay.InstallAsync();
        }

        // ── Toast ──

        public void Toast(string message, Action? undo = null, bool isError = false, string actionLabel = "Undo")
        {
            ToastText.Text = message;
            ToastMark.Mark = isError ? Mark.Danger : Mark.Ok;
            _toastAction = undo;
            ToastAction.Content = actionLabel;
            ToastAction.Visibility = undo != null ? Visibility.Visible : Visibility.Collapsed;
            ToastBox.Visibility = Visibility.Visible;
            ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(undo != null ? 10 : 6);
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) => ToastBox.Visibility = Visibility.Collapsed;
            ToastBox.BeginAnimation(OpacityProperty, fade);
        }

        private void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            var action = _toastAction;
            HideToast();
            action?.Invoke();
        }

        private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

        // ── Caption buttons ──

        private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            // A maximized borderless window overhangs the screen by the resize border
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        }

        // ── Screenshots for review (--capture <folder>) ──
        // The window is placed off screen and never activated, so it does not take focus from a running game.

        public async Task CaptureAsync(string folder, string size, string? pages)
        {
            var parts = size.Split('x');
            Width = double.Parse(parts[0]);
            Height = double.Parse(parts[1]);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            Top = -20000;
            ShowActivated = false;
            ShowInTaskbar = false;
            Show();

            Directory.CreateDirectory(folder);
            string[] ids = (pages ?? "home,maps,replays,hotkeys,health,addons,settings").Split(',');
            await Task.Delay(1500);
            if (_updateCheck != null)
                await _updateCheck;
            if (UpdateOverlay.Restarting)
                return;
            int n = 1;
            foreach (string entry in ids)
            {
                // "hotkeys:keys" opens a page and then one of its parts
                string id = entry.Split(':')[0];
                Navigate(id);
                await _pages[id].ReadyAsync();
                if (entry.Contains(':'))
                    _pages[id].ShowPart(entry.Split(':')[1]);
                await Task.Delay(900);
                SaveSnapshot(Path.Combine(folder, $"{n++:00}-{entry.Replace(':', '-')}.png"));
            }
            Application.Current.Shutdown();
        }

        public void SaveSnapshot(string path)
        {
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Round(Root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(Root.ActualHeight * dpi.DpiScaleY);
            var bitmap = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(Root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
