using System.Windows;
using System.Windows.Controls;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public partial class HealthPage : UserControl, IPage
    {
        private readonly TextBlock _scanned = new() { FontSize = 13, Foreground = Views.Res("Text2"), VerticalAlignment = VerticalAlignment.Center };
        private readonly DockPanel _footer;
        private bool _scanning;

        public HealthPage()
        {
            InitializeComponent();

            var chip = new Border { Style = Views.Style("Chip") };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Controls.Icon { Data = Views.Icon("clock"), Width = 15, Height = 15, Foreground = Views.Res("IconIdle"), Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(_scanned);
            chip.Child = row;
            TopRight = chip;

            _footer = new DockPanel { LastChildFill = true };
            var open = new Button { Content = "Open backups", Style = Views.Style("BtnLg") };
            Ui.SetIcon(open, Views.Icon("folder"));
            open.Click += (_, _) =>
            {
                System.IO.Directory.CreateDirectory(BackupService.Root);
                Views.ShowInFolder(BackupService.Root);
            };
            DockPanel.SetDock(open, Dock.Right);
            _footer.Children.Add(open);
            var hint = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            hint.Children.Add(new Controls.Icon { Data = Views.Icon("shield"), Width = 17, Height = 17, Foreground = Views.Res("IconIdle"), Margin = new Thickness(0, 0, 10, 0) });
            hint.Children.Add(new TextBlock { Text = "Checks only read your files. Fixes run when you press a button, and each one can be undone.", Foreground = Views.Res("Text2"), VerticalAlignment = VerticalAlignment.Center });
            _footer.Children.Add(hint);

            SizeChanged += (_, _) => SideCol.Width = new GridLength(ActualWidth < 1100 ? 320 : 380);
            AppState.HealthChanged += Show;
            BackupService.Changed += () => Dispatcher.Invoke(ShowHistory);
            Show();
            ShowHistory();
        }

        public string Title => "Health";
        public string Crumb => "Find and fix problems";
        public string IconKey => "I.shield";
        public FrameworkElement? TopRight { get; }
        public FrameworkElement? Footer => _footer;

        public void OnShown()
        {
            ShowHistory();
            UpdateScanned();
        }

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 40 && AppState.HealthCheckedAt == null; i++)
                await Task.Delay(250);
        }

        private void UpdateScanned() =>
            _scanned.Text = _scanning ? "Scanning…" : AppState.HealthCheckedAt is { } at ? "Last scan " + Views.Ago(at) : "Not scanned yet";

        private void Show()
        {
            var all = AppState.Health;
            var problems = all.Where(h => h.Status == HealthStatus.Problem).ToList();
            var warnings = all.Where(h => h.Status == HealthStatus.Warning).ToList();
            var passed = all.Where(h => h.Status == HealthStatus.Passed).ToList();

            Ring.Passed = passed.Count;
            Ring.Warnings = warnings.Count;
            Ring.Problems = problems.Count;
            Score.Text = all.Count == 0 ? "…" : $"{passed.Count}/{all.Count}";
            PassedCount.Text = passed.Count.ToString();
            WarnCount.Text = warnings.Count.ToString();
            ProblemCount.Text = problems.Count.ToString();
            WarnWord.Text = warnings.Count == 1 ? "Warning" : "Warnings";
            ProblemWord.Text = problems.Count == 1 ? "Problem" : "Problems";

            int issues = problems.Count + warnings.Count;
            Headline.Text = all.Count == 0 ? "Checking your game"
                : issues == 0 ? "Everything looks good"
                : problems.Count > 0 ? $"{problems.Count} {(problems.Count == 1 ? "problem" : "problems")} found"
                : $"{warnings.Count} {(warnings.Count == 1 ? "warning" : "warnings")}";
            Subline.Text = issues == 0 && all.Count > 0
                ? "Game files, Windows, settings and network all passed. Scan again after installing mods or patches."
                : "Nothing changes until you press a button. Every fix keeps a backup and can be undone.";

            var fixable = problems.Concat(warnings).Where(h => h.Fix != null).ToList();
            FixAllBtn.Content = $"Fix all ({fixable.Count})";
            FixAllBtn.Visibility = fixable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FixAllBtn.ToolTip = string.Join("\n", fixable.Select(f => "• " + (f.FixPreview ?? f.Title)));

            Attention.Children.Clear();
            AttentionPanel.Visibility = issues > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var result in problems.Concat(warnings))
                Attention.Children.Add(Row(result, attention: true));

            Passed.Children.Clear();
            Ui.SetSubtitle(PassedHead, passed.Count == 0 ? "Nothing yet" : $"{passed.Count} checks · {string.Join(", ", passed.GroupBy(p => p.Group).Select(g => $"{g.Key} {g.Count()}"))}");
            foreach (var group in passed.GroupBy(p => p.Group))
            {
                Passed.Children.Add(new TrackedText { Style = (Style)FindResource("LblAccent"), Text = group.Key, Margin = new Thickness(20, 14, 0, 4) });
                foreach (var result in group)
                    Passed.Children.Add(Row(result, attention: false));
            }
            UpdateScanned();
        }

        private FrameworkElement Row(HealthResult result, bool attention)
        {
            var row = Views.HealthRow(result, r =>
            {
                Views.ApplyFix(r);
            });
            var border = new Border
            {
                Child = row,
                Padding = attention ? new Thickness(20, 16, 20, 16) : new Thickness(20, 9, 20, 9),
                BorderBrush = Views.Res("Line"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = attention && result.Status == HealthStatus.Problem ? Views.Res("DangerSoft") : attention ? Views.Res("WarnSoft") : null,
            };
            return border;
        }

        private void ShowHistory()
        {
            History.Children.Clear();
            var entries = BackupService.Load().OrderByDescending(e => e.When).Take(8).ToList();
            if (entries.Count == 0)
            {
                History.Children.Add(new TextBlock { Text = "No fixes yet. Every change appears here with an Undo button.", Style = Views.Style("Body"), FontSize = 13, Margin = new Thickness(20, 12, 20, 8) });
                return;
            }
            foreach (var entry in entries)
            {
                var row = new DockPanel { Margin = new Thickness(20, 12, 20, 10) };
                var undo = new Button { Content = "Undo", Style = Views.Style("BtnXs"), IsEnabled = BackupService.CanUndo(entry), VerticalAlignment = VerticalAlignment.Top };
                undo.ToolTip = undo.IsEnabled ? "Put back what was there before" : "Changed again since, so this cannot be undone here";
                undo.Click += (_, _) =>
                {
                    if (BackupService.Undo(entry))
                        Views.Main.Toast("Undone: " + entry.Title);
                    _ = AppState.RefreshHealthAsync();
                };
                DockPanel.SetDock(undo, Dock.Right);
                row.Children.Add(undo);
                row.Children.Add(new TextBlock { Text = Views.Ago(entry.When), Width = 70, Style = Views.Style("Small"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
                var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                text.Children.Add(new TextBlock { Text = entry.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                if (entry.Detail.Length > 0)
                    text.Children.Add(new TextBlock { Text = entry.Detail, Style = Views.Style("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                row.Children.Add(text);
                History.Children.Add(row);
            }
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning)
                return;
            _scanning = true;
            ScanBtn.IsEnabled = false;
            UpdateScanned();
            try
            {
                await AppState.RefreshHealthAsync();
            }
            finally
            {
                _scanning = false;
                ScanBtn.IsEnabled = true;
                UpdateScanned();
            }
        }

        private void FixAll_Click(object sender, RoutedEventArgs e)
        {
            var fixable = AppState.Health.Where(h => h.Status != HealthStatus.Passed && h.Fix != null).ToList();
            if (fixable.Count == 0)
                return;
            if (fixable.All(f => f.Id.StartsWith("sample:")))
            {
                Views.Main.Toast("These are sample problems; nothing was changed.");
                return;
            }
            int before = BackupService.Load().Count;
            var failed = new List<string>();
            foreach (var result in fixable.Where(f => !f.Id.StartsWith("sample:")))
            {
                try { result.Fix!(); }
                catch (Exception ex) { failed.Add($"{result.Title}: {ex.Message}"); }
            }
            var made = BackupService.Load().Skip(before).ToList();
            if (failed.Count > 0)
                Views.Main.Toast("Some fixes failed: " + string.Join("; ", failed), isError: true);
            else
                Views.Main.Toast($"Applied {made.Count} {(made.Count == 1 ? "fix" : "fixes")}.", () =>
                {
                    foreach (var entry in Enumerable.Reverse(made))
                        BackupService.Undo(entry);
                    _ = AppState.RefreshHealthAsync();
                    Views.Main.Toast("All fixes undone");
                });
            _ = AppState.RefreshHealthAsync();
        }

        private void TogglePassed_Click(object sender, RoutedEventArgs e)
        {
            bool show = Passed.Visibility != Visibility.Visible;
            Passed.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TogglePassed.Content = show ? "Hide" : "Show";
            Ui.SetIcon(TogglePassed, Views.Icon(show ? "chevd" : "chev"));
        }

        private void Report_Click(object sender, RoutedEventArgs e) => Views.CopySupportReport();
    }
}
