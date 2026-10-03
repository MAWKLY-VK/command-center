using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using CommandCenter.Pages;
using CommandCenter.Services;

namespace CommandCenter
{
    public partial class MainWindow : Window
    {
        private LauncherPage? _home;
        private ToolsPage? _tools;
        private OptionsPage? _options;
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private Action? _toastAction;
        private Task? _updateCheck;
        private bool _selectingTab;
        private readonly bool _still = App.HasArg("--capture");
        private readonly List<Storyboard> _ambient = new();
        private int _shownPlayers;

        public MainWindow()
        {
            InitializeComponent();
            _toastTimer.Tick += (_, _) => HideToast();
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
            // Near the smallest size the tabs need the room, so the player count drops its word
            SizeChanged += (_, e) => OnlineLabel.Visibility = e.NewSize.Width < 1060 ? Visibility.Collapsed : Visibility.Visible;
            LoadBackdrop();
            AppState.StatsChanged += () => Dispatcher.Invoke(ShowOnline);
            AppState.HealthChanged += () => Dispatcher.Invoke(ShowHealthBadge);
            ShowOnline();
            ShowHealthBadge();
            if (!_still)
                StartAmbient();
            ShowHome();

            // Screenshots skip the update check unless a manifest is given
            if (App.Arg("--update-url") != null || !App.HasArg("--capture"))
                _updateCheck = CheckUpdateAsync();
        }

        // ── Navigation ──

        public void ShowHome()
        {
            SelectTab(TabHome);
            Navigate(_home ??= new LauncherPage());
        }

        public void ShowTools(string? section = null)
        {
            _tools ??= new ToolsPage();
            if (section != null)
                _tools.Select(section);
            SelectTab(_tools.Current switch
            {
                "maps" => TabMaps,
                "replays" => TabReplays,
                "hotkeys" => TabHotkeys,
                "addons" => TabAddons,
                _ => TabHealth,
            });
            Navigate(_tools);
        }

        public void ShowOptions(string? part = null)
        {
            SelectTab(null);
            Navigate(_options ??= new OptionsPage());
            if (part != null)
                _options.ShowPart(part);
        }

        private void Navigate(Page page)
        {
            // Leaving the Options page by a tab keeps what was set there, as its own Save button does
            if (ContentFrame.Content is OptionsPage options && !ReferenceEquals(options, page))
                options.Commit();
            if (ReferenceEquals(ContentFrame.Content, page))
            {
                ShowBackdropFor(page);
                return;
            }
            ContentFrame.Navigate(page);
        }

        private void SelectTab(RadioButton? tab)
        {
            _selectingTab = true;
            foreach (var other in new[] { TabHome, TabHealth, TabMaps, TabReplays, TabHotkeys, TabAddons })
                other.IsChecked = ReferenceEquals(other, tab);
            _selectingTab = false;
            if (tab == null)
                OptionsButton.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFE, 0xCD, 0x03));
            else
                OptionsButton.ClearValue(BorderBrushProperty);
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (_selectingTab || sender is not RadioButton { Tag: string id })
                return;
            if (id == "home")
                ShowHome();
            else
                ShowTools(id);
        }

        private void Options_Click(object sender, RoutedEventArgs e) => ShowOptions();

        // A Frame does not pass FlowDirection on to its pages, so each page gets it here
        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (e.Content is FrameworkElement page)
            {
                page.FlowDirection = Loc.FlowDirection;
                ShowBackdropFor(page);
                Enter(page);
            }
            if (e.Content is ToolsPage tools)
                tools.OnShown();
            while (ContentFrame.CanGoBack)
                ContentFrame.RemoveBackEntry();
        }

        // ── Backdrop and motion ──

        // The game's art from the Steam library; other copies get the drawn background
        private void LoadBackdrop()
        {
            try
            {
                // --drawn-backdrop shows the background other copies get, for screenshots
                if (!App.HasArg("--drawn-backdrop") && GamePaths.SteamArt("library_hero.jpg") is { } hero)
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(hero);
                    image.DecodePixelWidth = 1920;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    HeroArt.Source = image;
                    return;
                }
            }
            catch { }
            HeroArt.Visibility = Visibility.Collapsed;
            DrawnBackdrop.Visibility = Visibility.Visible;
        }

        // The art shows in full on the home page and steps back, blurred and dimmed, behind the tools
        private void ShowBackdropFor(FrameworkElement page)
        {
            bool home = page is LauncherPage;
            var time = _still ? TimeSpan.Zero : TimeSpan.FromMilliseconds(450);
            ToolScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(home ? 0 : 1, time));
            HeroBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(home ? 0 : 10, time));
        }

        // Pages slide up a little as they appear
        private void Enter(FrameworkElement page)
        {
            if (_still)
                return;
            var move = new TranslateTransform(0, 14);
            page.RenderTransform = move;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
        }

        private void StartAmbient()
        {
            var zoom = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
            foreach (string axis in new[] { "ScaleX", "ScaleY" })
            {
                var grow = new DoubleAnimation(1.03, 1.12, TimeSpan.FromSeconds(26)) { EasingFunction = new SineEase() };
                Storyboard.SetTarget(grow, HeroArt);
                Storyboard.SetTargetProperty(grow, new PropertyPath("RenderTransform." + axis));
                zoom.Children.Add(grow);
            }
            var pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            foreach (string axis in new[] { "ScaleX", "ScaleY" })
            {
                var spread = new DoubleAnimation(1, 2.4, TimeSpan.FromSeconds(1.6)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(spread, PulseRing);
                Storyboard.SetTargetProperty(spread, new PropertyPath("RenderTransform." + axis));
                pulse.Children.Add(spread);
            }
            var fade = new DoubleAnimation(0.6, 0, TimeSpan.FromSeconds(1.6));
            Storyboard.SetTarget(fade, PulseRing);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            pulse.Children.Add(fade);
            _ambient.Add(zoom);
            _ambient.Add(pulse);
            foreach (var storyboard in _ambient)
                storyboard.Begin(this, true);
        }

        // Nothing moves while the window is minimized (during a match)
        private void PauseAmbient(bool pause)
        {
            foreach (var storyboard in _ambient)
            {
                if (pause)
                    storyboard.Pause(this);
                else
                    storyboard.Resume(this);
            }
        }

        // Players online, counted up to the new number
        private void ShowOnline()
        {
            if (AppState.Stats is not { } stats)
                return;
            OnlinePill.Visibility = Visibility.Visible;
            int from = _shownPlayers, to = stats.Players;
            _shownPlayers = to;
            if (_still || from == to)
            {
                OnlineCount.Text = to.ToString("N0");
                return;
            }
            var start = DateTime.Now;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 1100);
                double eased = 1 - Math.Pow(1 - t, 3);
                OnlineCount.Text = ((int)Math.Round(from + (to - from) * eased)).ToString("N0");
                if (t >= 1)
                    timer.Stop();
            };
            timer.Start();
        }

        private void ShowHealthBadge()
        {
            int count = AppState.ProblemCount + AppState.WarningCount;
            HealthBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HealthBadge.Background = new SolidColorBrush(AppState.ProblemCount > 0 ? Color.FromRgb(0xFF, 0x5A, 0x5A) : Color.FromRgb(0xFF, 0xB0, 0x20));
            HealthBadgeText.Text = count.ToString();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (UpdateOverlay.IsOpen)
                UpdateOverlay.OnKey(e);
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (ContentFrame.Content is OptionsPage options)
                options.Commit();
            if (UpdateOverlay.Restarting || _tools == null || !_tools.HasPendingChanges)
                return;
            var answer = MessageBox.Show(this, Loc.T("You have unsaved changes. Close without saving?"), "Command Center",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, Loc.MessageBoxOptions);
            if (answer != MessageBoxResult.Yes)
                e.Cancel = true;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            // A maximized borderless window overhangs the screen by the resize border
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            PauseAmbient(WindowState == WindowState.Minimized);
        }

        // ── Toast ──

        public void Toast(string message, Action? undo = null, bool isError = false)
        {
            ToastText.Text = message;
            ToastText.Foreground = isError ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) : Brushes.White;
            ToastBox.BorderBrush = isError ? new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)) : new SolidColorBrush(Color.FromRgb(0x29, 0x80, 0xFF));
            _toastAction = undo;
            ToastAction.Content = Loc.T("UNDO");
            ToastAction.Visibility = undo != null ? Visibility.Visible : Visibility.Collapsed;
            ToastBox.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(undo != null ? 10 : 6);
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            ToastBox.Visibility = Visibility.Collapsed;
        }

        private void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            var action = _toastAction;
            HideToast();
            action?.Invoke();
        }

        private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

        // ── Mandatory update ──

        private static string UpdateSource => App.Arg("--update-url") ?? UpdateService.ManifestUrl;

        private async Task CheckUpdateAsync()
        {
            if (await UpdateService.CheckAsync(UpdateSource) is not { } update)
                return;
            UpdateOverlay.Open(update);
            if (App.HasArg("--update-auto"))
                await UpdateOverlay.InstallAsync();
        }

        // The Options page's check: true when a newer version was found (the update screen is then shown)
        public async Task<bool> CheckForUpdateNowAsync()
        {
            if (await UpdateService.CheckAsync(UpdateSource) is not { } update)
                return false;
            UpdateOverlay.Open(update);
            return true;
        }

        // ── Screenshots for review (--capture <folder>) ──
        // The window is placed off screen and never activated, so it does not take focus from a running game.
        // Page ids: home, options, tools:health, tools:maps, tools:replays, tools:hotkeys, tools:addons

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
            string[] ids = (pages ?? "home,tools:health,tools:maps,tools:replays,tools:hotkeys,tools:addons,options").Split(',');
            await Task.Delay(1500);
            if (_updateCheck != null)
                await _updateCheck;
            if (UpdateOverlay.Restarting)
                return;
            int n = 1;
            foreach (string entry in ids)
            {
                string[] id = entry.Split(':');
                switch (id[0])
                {
                    case "options":
                        ShowOptions();
                        if (id.Length > 1)
                        {
                            await Task.Delay(300);
                            _options!.ShowPart(id[1]);
                        }
                        break;
                    case "tools":
                        ShowTools(id.Length > 1 ? id[1] : null);
                        if (id.Length > 2)
                            _tools!.ShowPart(id[2]);
                        await _tools!.ReadyAsync();
                        break;
                    default:
                        ShowHome();
                        // The stats line and the health notice arrive in the background
                        for (int wait = 0; wait < 40 && (AppState.Stats == null || AppState.HealthCheckedAt == null); wait++)
                            await Task.Delay(500);
                        break;
                }
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
            if (FlowDirection == FlowDirection.RightToLeft)
            {
                // The right-to-left mirror sits on the window, not on Root; add it so the picture matches the screen
                var area = Root.RenderSize;
                var mirrored = new DrawingVisual();
                using (var dc = mirrored.RenderOpen())
                {
                    dc.PushTransform(new MatrixTransform(-1, 0, 0, 1, area.Width, 0));
                    dc.DrawRectangle(new VisualBrush(Root) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(area),
                        AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(area));
                }
                bitmap.Render(mirrored);
            }
            else
            {
                bitmap.Render(Root);
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
