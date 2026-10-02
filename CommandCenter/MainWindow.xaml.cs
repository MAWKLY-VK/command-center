using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        public MainWindow()
        {
            InitializeComponent();
            _toastTimer.Tick += (_, _) => HideToast();
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
            ShowHome();

            // Screenshots skip the update check unless a manifest is given
            string? updateSource = App.Arg("--update-url");
            if (updateSource != null || !App.HasArg("--capture"))
                _updateCheck = CheckUpdateAsync(updateSource ?? UpdateService.ManifestUrl);
        }

        // ── Navigation ──

        public void ShowHome() => ContentFrame.Navigate(_home ??= new LauncherPage());

        public void ShowTools(string? section = null)
        {
            _tools ??= new ToolsPage();
            if (section != null)
                _tools.Select(section);
            ContentFrame.Navigate(_tools);
        }

        public void ShowOptions() => ContentFrame.Navigate(_options ??= new OptionsPage());

        // A Frame does not pass FlowDirection on to its pages, so each page gets it here
        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (e.Content is FrameworkElement page)
                page.FlowDirection = Loc.FlowDirection;
            if (e.Content is ToolsPage tools)
                tools.OnShown();
            while (ContentFrame.CanGoBack)
                ContentFrame.RemoveBackEntry();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (UpdateOverlay.IsOpen)
                UpdateOverlay.OnKey(e);
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
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

        private async Task CheckUpdateAsync(string source)
        {
            if (await UpdateService.CheckAsync(source) is not { } update)
                return;
            UpdateOverlay.Open(update);
            if (App.HasArg("--update-auto"))
                await UpdateOverlay.InstallAsync();
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
                        break;
                    case "tools":
                        ShowTools(id.Length > 1 ? id[1] : null);
                        if (id.Length > 2)
                            _tools!.ShowPart(id[2]);
                        await _tools!.ReadyAsync();
                        break;
                    default:
                        ShowHome();
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
