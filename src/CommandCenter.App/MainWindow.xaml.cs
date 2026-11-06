using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using CommandCenter.Imaging;
using CommandCenter.Pages;

namespace CommandCenter
{
    public partial class MainWindow : Window, IShell
    {
        private LauncherPage? _home;
        private ToolsPage? _tools;
        private OptionsPage? _options;
        private HelpPage? _help;
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private (string Message, Action? Undo, bool IsError)? _afterNotice;
        private Action? _toastAction;
        private Task? _updateCheck;
        private Task? _englishOnlyStep;
        private Task? _artLoad;
        private bool _selectingTab;
        private bool _onHome = true;
        private readonly bool _still = App.HasArg("--capture");
        private Storyboard? _zoom;
        private HomeImage? _shownImage;

        public MainWindow()
        {
            InitializeComponent();
            _toastTimer.Tick += (_, _) => HideToast();
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
            Grain.Fill = ArtLayers.Grain();
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                    Dispatcher.Invoke(UpdateZoom);
            };
            AppState.HealthChanged += () => Dispatcher.Invoke(ShowHealthBadge);
            // Another game folder brings its own picture; a picture pinned in Settings shows at once
            AppServices.GameFolder.Changed += () => _artLoad = LoadArtAsync(AppSettings.Current.HomeImage, remember: true);
            AppServices.Appearance.Changed += () =>
            {
                var pinned = AppServices.Appearance.HomeImage;
                if (pinned != HomeImage.Changing && pinned != _shownImage)
                    _artLoad = LoadArtAsync(pinned, remember: true);
                UpdateZoom();
            };
            ShowHealthBadge();
            _artLoad = LoadArtAsync(AppSettings.Current.HomeImage, remember: true);
            ShowHome();

            // Screenshots skip the update check unless a manifest is given
            if (App.Arg("--update-url") != null || !App.HasArg("--capture"))
                _updateCheck = CheckUpdateAsync();
            bool stepDue = !AppSettings.Current.EnglishOnlyStepDone;
            _englishOnlyStep = RunEnglishOnlyStepAsync();
            if (stepDue)
                AppState.ReadHotkeysAfter(_englishOnlyStep);
        }

        // ── Navigation (P5-D1): Home, Replays, Maps, Hotkeys and Health, then Settings and Help ──

        public void ShowHome()
        {
            SelectTab(TabHome);
            Navigate(_home ??= new LauncherPage());
        }

        // Replays, Maps, Hotkeys or Health. Add-ons is a section of Settings.
        public void ShowTools(string? section = null)
        {
            if (section == "addons")
            {
                ShowOptions("addons");
                return;
            }
            _tools ??= new ToolsPage();
            if (section != null)
                _tools.Select(section);
            SelectTab(_tools.Current switch
            {
                "maps" => TabMaps,
                "replays" => TabReplays,
                "hotkeys" => TabHotkeys,
                _ => TabHealth,
            });
            Navigate(_tools);
        }

        // Health with its checks running again, as a common question's "Run the checks" asks
        public void RunChecks()
        {
            ShowTools("health");
            _tools!.RunHealthChecks();
        }

        // The common questions, the support report and where to ask; with the game not found, where to get it comes first
        public void ShowHelp()
        {
            SelectTab(TabHelp);
            Navigate(_help ??= new HelpPage());
        }

        public void ShowQuestions() => ShowHelp();

        public void ShowOptions(string? part = null)
        {
            SelectTab(TabSettings);
            Navigate(_options ??= new OptionsPage());
            if (part != null)
                _options.ShowPart(part);
        }

        private void Navigate(Page page)
        {
            // Leaving Settings by a tab keeps what was set there, as its own Save button does
            if (ContentFrame.Content is OptionsPage options && !ReferenceEquals(options, page))
                options.Commit();
            if (ReferenceEquals(ContentFrame.Content, page))
            {
                ShowArtFor(page);
                return;
            }
            ContentFrame.Navigate(page);
        }

        private void SelectTab(RadioButton tab)
        {
            _selectingTab = true;
            foreach (var other in new[] { TabHome, TabReplays, TabMaps, TabHotkeys, TabHealth, TabSettings, TabHelp })
                other.IsChecked = ReferenceEquals(other, tab);
            _selectingTab = false;
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (_selectingTab || sender is not RadioButton { Tag: string id })
                return;
            switch (id)
            {
                case "home":
                    ShowHome();
                    break;
                case "settings":
                    ShowOptions();
                    break;
                case "help":
                    ShowHelp();
                    break;
                default:
                    ShowTools(id);
                    break;
            }
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (e.Content is FrameworkElement page)
                ShowArtFor(page);
            if (e.Content is ToolsPage tools)
                tools.OnShown();
            while (ContentFrame.CanGoBack)
                ContentFrame.RemoveBackEntry();
        }

        // ── The art (phase 5 spec) ──

        // The picture for this start (or the one asked for), tinted, with its blurred copy for the other pages. The drawn
        // background stands in when the game or its art is missing. The reading and tinting run off the interface thread.
        private async Task LoadArtAsync(HomeImage setting, bool remember)
        {
            var scene = new ArtTint(TokenRgb("Color.Art.Shadow"), TokenRgb("Color.Art.Highlight"), (double)FindResource("Art.Keep"),
                (double)FindResource("Art.Brightness.Scene"));
            var emblem = scene with { Brightness = (double)FindResource("Art.Brightness.Emblem") };
            double backdrop = (double)FindResource("Art.Backdrop");
            var last = AppSettings.Current.LastHomeImage;
            bool drawn = App.HasArg("--drawn-backdrop") || !GamePaths.ZeroHourFound;
            var art = drawn ? null : await Task.Run(() => PrepareArt(setting, last, scene, emblem, backdrop));

            DrawnBackdrop.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
            HomeArtImage.Source = art?.Home;
            PagesArt.Source = art?.Pages;
            _shownImage = art?.Image;
            if (art != null && remember)
            {
                AppSettings.Current.LastHomeImage = art.Value.Image;
                AppSettings.Current.Save();
            }
            ShowArtFor(ContentFrame.Content as FrameworkElement);
        }

        // The battle scene gets the scene's tint, the three faction loading screens the darker emblem tint
        private static (HomeImage Image, BitmapSource Home, BitmapSource Pages)? PrepareArt(HomeImage setting, HomeImage? last, ArtTint scene,
            ArtTint emblem, double backdrop)
        {
            try
            {
                var images = new GameImages(new GameFiles(GamePaths.Game), HotkeyService.DetectLanguage());
                if (HomeArt.Pick(setting, last, image => HomeArt.Load(images, image)) is not { } pick)
                    return null;
                var tinted = HomeArt.Tint(pick.Picture, pick.Image == HomeImage.MainMenu ? scene : emblem);
                return (pick.Image, tinted.ToBitmap(), HomeArt.Backdrop(tinted, backdrop).ToBitmap());
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                return null;
            }
        }

        private Rgb TokenRgb(string key) => FindResource(key) is Color c ? new Rgb(c.R, c.G, c.B) : default;

        // Home shows the picture with its scrims; the other pages sit on the blurred copy, or on the drawn background
        // dimmed. The change is a short fade, and none when Windows' animation effects are off.
        private void ShowArtFor(FrameworkElement? page)
        {
            _onHome = page is null or LauncherPage;
            bool art = HomeArtImage.Source != null;
            var time = _still || !AppServices.Appearance.AnimationsOn ? TimeSpan.Zero : ((Duration)FindResource("Motion.State")).TimeSpan;
            Fade(HomeArtImage, art && _onHome ? 1 : 0, time);
            Fade(HomeScrims, _onHome ? 1 : 0, time);
            Fade(PagesArt, art && !_onHome ? 1 : 0, time);
            Fade(PagesScrim, !art && !_onHome ? 1 : 0, time);
            UpdateZoom();
        }

        private static void Fade(UIElement element, double to, TimeSpan time) =>
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, time));

        // The one continuous motion (plan 3-c): Home's picture zooms in and out very slowly while Home shows. It stops
        // while the window is minimized, when Windows' animation effects are off, and for screenshots.
        private void UpdateZoom()
        {
            bool run = _onHome && HomeArtImage.Source != null && WindowState != WindowState.Minimized
                       && AppServices.Appearance.AnimationsOn && !_still;
            if (!run)
            {
                _zoom?.Pause(this);
                return;
            }
            if (_zoom != null)
            {
                _zoom.Resume(this);
                return;
            }
            _zoom = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
            double to = (double)FindResource("Art.Zoom");
            var duration = (Duration)FindResource("Motion.Zoom");
            foreach (string axis in new[] { "ScaleX", "ScaleY" })
            {
                var grow = new DoubleAnimation(1, to, duration) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Storyboard.SetTarget(grow, HomeArtImage);
                Storyboard.SetTargetProperty(grow, new PropertyPath("RenderTransform." + axis));
                _zoom.Children.Add(grow);
            }
            _zoom.Begin(this, true);
        }

        private void ShowHealthBadge()
        {
            var report = AppServices.Health.Report;
            int count = report.BadgeCount;
            HealthBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HealthBadge.Background = (Brush)FindResource(report.BadgeIsProblem ? "Problem" : "Warning");
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
            bool pending = _tools?.HasPendingChanges == true || _options?.HasPendingChanges == true;
            if (UpdateOverlay.Restarting || !pending)
                return;
            var answer = MessageBox.Show(this, "You have unsaved changes. Close without saving?", "Command Center",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
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
            UpdateZoom();
        }

        // ── Toast ──

        public void Toast(string message, Action? undo = null, bool isError = false)
        {
            // The one-time notice stays until it is closed; the newest toast that comes meanwhile is shown after it
            if (ToastArabic.Visibility == Visibility.Visible && ToastBox.Visibility == Visibility.Visible)
            {
                _afterNotice = (message, undo, isError);
                return;
            }
            ToastText.Text = message;
            ToastIcon.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
            if (isError)
                ToastBox.BorderBrush = (Brush)FindResource("Problem");
            else
                ToastBox.ClearValue(Border.BorderBrushProperty);
            _toastAction = undo;
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
            ToastArabic.Visibility = Visibility.Collapsed;
            if (_afterNotice is { } next)
            {
                _afterNotice = null;
                Toast(next.Message, next.Undo, next.IsError);
            }
        }

        private void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            var action = _toastAction;
            HideToast();
            action?.Invoke();
        }

        private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

        // A toast that stays until it is closed, with an Arabic paragraph above the English one
        private void Notice(string arabic, string english, Action? undo)
        {
            Toast(english, undo);
            _toastTimer.Stop();
            ToastArabic.Text = arabic;
            ToastArabic.Visibility = Visibility.Visible;
        }

        // ── The English-only version's one-time step ──
        // It waits for the update check: when a newer version was found, the step is left to that version, so the
        // notice is never hidden behind the update screen.

        private async Task RunEnglishOnlyStepAsync()
        {
            try
            {
                if (_updateCheck != null)
                    await _updateCheck;
                if (UpdateOverlay.IsOpen)
                    return;
                bool windowsArabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
                var result = await Task.Run(() => ArabicGameTextRemoval.RunOnce(AppSettings.Current, windowsArabic));
                if (result == null)
                    return;
                if (result.Changes.Count > 0)
                    _ = AppState.RefreshHealthAsync();
                if (result.NoticeDue)
                    ShowEnglishOnlyNotice(result.Changes);
            }
            catch (Exception ex)
            {
                // The step runs again at the next start
                ErrorLog.Write(ex);
            }
        }

        // The only Arabic text in Command Center: the one-time notice for players who used 0.4.0 in Arabic or had its
        // Arabic game text
        private void ShowEnglishOnlyNotice(List<BackupEntry> changes)
        {
            bool changed = changes.Count > 0;
            string arabic = "يعمل Command Center بالإنجليزية فقط، وقد أُزيلت واجهته العربية."
                + (changed ? " وأُعيد نص اللعبة إلى الإنجليزية مع الإبقاء على اختصاراتك المحفوظة. زر UNDO يعيد نص اللعبة العربي." : "");
            string english = "Command Center is in English only. Its Arabic interface was removed."
                + (changed ? " The game's text is in English again, with your saved hotkeys kept. UNDO puts the Arabic game text back." : "");
            Notice(arabic, english, changed ? () => UndoArabicGameTextRemoval(changes) : null);
        }

        private void UndoArabicGameTextRemoval(List<BackupEntry> changes)
        {
            if (!ArabicGameTextRemoval.TryUndo(changes, out string reason))
            {
                Toast(reason, isError: true);
                return;
            }
            AppState.ForgetHotkeys();
            _ = AppState.RefreshHealthAsync();
            Toast("The Arabic game text is back. Health can remove it later.");
        }

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

        // ── The shell the view models ask (IShell) ──

        // Settings' check: the update screen opens when a newer version was found
        public async Task<UpdateCheck> CheckForUpdateAsync()
        {
            var check = await UpdateService.CheckNowAsync(UpdateSource);
            if (check.Update != null)
                UpdateOverlay.Open(check.Update);
            return check;
        }

        public string? ChooseFolder(string title, string? initial)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (initial != null)
                dialog.InitialDirectory = initial;
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }

        public void Warn(string message) =>
            MessageBox.Show(this, message, "Command Center", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);

        public void RefreshHealth() => _ = AppState.RefreshHealthAsync();

        public bool Confirm(string message) =>
            MessageBox.Show(this, message, "Command Center", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;

        public IntPtr WindowHandle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // Minimized without taking the focus, so a full-screen game keeps it
        public void StepAside() => ShowWindow(WindowHandle, MinimizedNoActivate);

        public void ComeBack()
        {
            WindowState = WindowState.Normal;
            Activate();
        }

        private const int MinimizedNoActivate = 7;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        // ── Screenshots for review (--capture <folder>) ──
        // The window is placed off screen and never activated, so it does not take focus from a running game.
        // Page ids: home, home:<picture> (mainmenu, usa, china or gla), replays, maps[:installed|library],
        // hotkeys[:buttons|keys|letters], health[:history], settings[:<section>[:run]] (run also runs the network
        // diagnostics first), help

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
            string[] ids = (pages ?? "home,replays,maps,hotkeys,health,settings,help").Split(',');
            await Task.Delay(1500);
            if (_updateCheck != null)
                await _updateCheck;
            if (_englishOnlyStep != null)
                await _englishOnlyStep;
            if (_artLoad != null)
                await _artLoad;
            if (UpdateOverlay.Restarting)
                return;
            int n = 1;
            foreach (string entry in ids)
            {
                string[] id = entry.Split(':');
                switch (id[0])
                {
                    case "settings":
                        ShowOptions();
                        if (id.Length > 1)
                        {
                            await Task.Delay(300);
                            _options!.ShowPart(id[1]);
                            // "run" runs the network diagnostics first; "end" shows the end of a long section
                            if (id.Skip(2).Contains("run"))
                                await _options.RunDiagnosticsAsync();
                            if (id.Skip(2).Contains("end"))
                                _options.ScrollToEnd();
                        }
                        break;
                    case "help":
                        ShowHelp();
                        break;
                    case "replays" or "maps" or "hotkeys" or "health":
                        ShowTools(id[0]);
                        if (id.Length > 1)
                            _tools!.ShowPart(id[1]);
                        await _tools!.ReadyAsync();
                        // "end" shows the end of a long section, once its data is there
                        if (id.Skip(1).Contains("end"))
                            _tools.ScrollToEnd();
                        break;
                    default:
                        if (id.Length > 1 && Enum.TryParse(id[1], ignoreCase: true, out HomeImage picture))
                            await LoadArtAsync(picture, remember: false);
                        ShowHome();
                        // The stats line and the health notice arrive in the background
                        for (int wait = 0; wait < 40 && (AppState.Stats == null || AppState.HealthCheckedAt == null); wait++)
                            await Task.Delay(500);
                        // home:locate, home:update, home:fix or home:play shows PLAY in that state for review
                        if (id.Length > 1)
                            _home!.PreviewPlay(id[1]);
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
            bitmap.Render(Root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
