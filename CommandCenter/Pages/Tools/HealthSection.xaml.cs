using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages.Tools
{
    // Health: the checks that need attention first, each with its fix, then everything that passed by area;
    // and the history of changes the launcher made, each with Undo.
    public partial class HealthSection : UserControl, IToolSection
    {
        private static readonly SolidColorBrush DetailBrush = Views.Brush("#BCC2D8");
        private static readonly SolidColorBrush CardBrush = Views.Brush("#B30A0E20");
        private static readonly SolidColorBrush ChipText = Views.Brush("#C6EED2");
        private static readonly SolidColorBrush PassedBrush = Views.Brush("#D0D0E0");
        private static readonly SolidColorBrush PreviewBrush = Views.Brush("#7FA6E8");
        private static readonly SolidColorBrush LineBrush = Views.Brush("#14FFFFFF");

        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
        private bool _checking;
        private bool _historyQueued;
        private bool _historyStale = true;
        private int _historyVersion;

        public HealthSection()
        {
            InitializeComponent();
            TabChecks.Content = Loc.T("CHECKS");
            CheckButton.ToolTip = Loc.T("Runs every check again. Nothing is changed.");
            AppState.HealthChanged += () => Dispatcher.Invoke(ShowResults);
            BackupService.Changed += () => Dispatcher.BeginInvoke(QueueHistory);
            _clock.Tick += (_, _) => ShowCheckedAt();
            Loaded += (_, _) => _clock.Start();
            Unloaded += (_, _) => _clock.Stop();
            ShowResults();
            ShowHistory();
            ShowView();
        }

        public bool HasPendingChanges => false;

        public void OnShown()
        {
            ShowCheckedAt();
            if (_checking)
                return;
            if (AppState.HealthCheckedAt is { } at)
            {
                // Results older than a few minutes may no longer match the files
                if (DateTime.Now - at > TimeSpan.FromMinutes(10))
                    _ = CheckAsync();
            }
            else
            {
                // The home page starts the first check; start one here only if it never arrives
                _ = CheckIfNoneArrivesAsync();
            }
        }

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 120 && (AppState.HealthCheckedAt == null || _checking); i++)
                await Task.Delay(250);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        public void ShowPart(string part)
        {
            if (part == "history")
                TabHistory.IsChecked = true;
            else
                TabChecks.IsChecked = true;
        }

        // ── Checks ──

        private void ShowResults()
        {
            var all = AppState.Health;
            var problems = all.Where(h => h.Status == HealthStatus.Problem).ToList();
            var warnings = all.Where(h => h.Status == HealthStatus.Warning).ToList();
            var passed = all.Where(h => h.Status == HealthStatus.Passed).ToList();
            bool ready = AppState.HealthCheckedAt != null;
            int issues = problems.Count + warnings.Count;

            if (!ready)
            {
                Summary.Text = Loc.T("Checking your game…");
            }
            else if (issues == 0)
            {
                Summary.Text = Loc.N(all.Count, "{0} check passed. Check again after installing mods or patches.",
                    "All {0} checks passed. Check again after installing mods or patches.");
            }
            else
            {
                var found = new List<string>();
                if (problems.Count > 0)
                    found.Add(Loc.N(problems.Count, "{0} problem", "{0} problems"));
                if (warnings.Count > 0)
                    found.Add(Loc.N(warnings.Count, "{0} warning", "{0} warnings"));
                Summary.Text = Loc.T("{0} found.", Loc.List(found)) + " " + Loc.T("Nothing changes until you press a button, and every fix can be undone.");
            }

            var fixable = problems.Concat(warnings).Where(h => h.Fix != null).ToList();
            FixAllText.Text = Loc.T("FIX ALL ({0})", fixable.Count);
            FixAllButton.Visibility = fixable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FixAllButton.ToolTip = fixable.Count > 0 ? string.Join("\n", fixable.Select(f => "• " + Plain(f.FixPreview ?? f.Title))) : null;

            ShowRing(ready, passed.Count, warnings.Count, problems.Count);

            ChecksList.Children.Clear();
            if (!ready)
            {
                ChecksList.Children.Add(Message(Loc.T("Checking game files, Windows, settings and network…")));
            }
            else
            {
                AddIssues(Loc.T("Problems"), problems);
                AddIssues(Loc.T("Warnings"), warnings);
                foreach (var group in passed.GroupBy(p => p.Group))
                    AddPassed(Loc.T(group.Key), group.ToList());
            }
            ShowCheckedAt();
        }

        // The ring fills to the share of checks that passed: green when nothing is wrong, amber for warnings, red for problems
        private void ShowRing(bool ready, int passed, int warnings, int problems)
        {
            int all = passed + warnings + problems;
            PassedCount.Text = ready ? passed.ToString(Loc.Culture) : "—";
            WarningCount.Text = ready ? warnings.ToString(Loc.Culture) : "—";
            ProblemCount.Text = ready ? problems.ToString(Loc.Culture) : "—";
            if (!ready || all == 0)
            {
                PercentText.Text = "—";
                RingLabel.Text = Loc.T("Checking…");
                Ring.Visibility = Visibility.Hidden;
                return;
            }

            double share = (double)passed / all;
            PercentText.Text = $"{Math.Round(share * 100)}%";
            RingLabel.Text = problems > 0 ? Loc.T("needs fixing") : warnings > 0 ? Loc.T("playable") : Loc.T("healthy");
            Ring.Stroke = problems > 0 ? Views.Problem : warnings > 0 ? Views.Warning : Views.Passed;
            Ring.Visibility = share > 0 ? Visibility.Visible : Visibility.Hidden;

            // The dash runs along the ring's centre line, measured in stroke widths
            double length = Math.PI * (140 - Ring.StrokeThickness) / Ring.StrokeThickness;
            Ring.StrokeDashArray = new DoubleCollection { length, length * 2 };
            double to = length * (1 - share);
            if (App.HasArg("--capture"))
                Ring.StrokeDashOffset = to;
            else
                Ring.BeginAnimation(Shape.StrokeDashOffsetProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(length, to, TimeSpan.FromMilliseconds(900))
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                    });
        }

        private void AddIssues(string name, List<HealthResult> results)
        {
            if (results.Count == 0)
                return;
            ChecksList.Children.Add(GroupLabel(name));
            foreach (var result in results)
                ChecksList.Children.Add(IssueCard(result));
        }

        // Passed checks of one area as small green chips; what was found shows on hover
        private void AddPassed(string name, List<HealthResult> results)
        {
            ChecksList.Children.Add(GroupLabel(name));
            var chips = new WrapPanel();
            foreach (var result in results)
            {
                if (ActionButton(result) != null)
                    ChecksList.Children.Add(PassedRow(result));
                else
                    chips.Children.Add(PassedChip(result));
            }
            if (chips.Children.Count > 0)
                ChecksList.Children.Add(chips);
        }

        private TextBlock GroupLabel(string name) => new()
        {
            Text = Upper(name),
            Style = (Style)FindResource("GroupLabelStyle"),
            Margin = new Thickness(2, ChecksList.Children.Count == 0 ? 0 : 14, 0, 8),
        };

        // A problem or warning: what is wrong, why it matters, what the fix will do, and the fix
        private FrameworkElement IssueCard(HealthResult result)
        {
            var color = Views.StatusBrush(result.Status).Color;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(12),
                Background = Tint(color, 0x24),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 14, 0),
                ToolTip = Views.StatusWord(result.Status),
                Child = new Controls.Icon
                {
                    Kind = result.Status == HealthStatus.Problem ? "x-circle" : "alert",
                    Width = 20,
                    Height = 20,
                    Foreground = new SolidColorBrush(color),
                },
            });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = Plain(result.Title), FontSize = 14, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            if (result.Detail.Length > 0)
                text.Children.Add(new TextBlock { Text = Plain(result.Detail), FontSize = 12, Foreground = DetailBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            if (result.Note != null)
                text.Children.Add(new TextBlock { Text = Plain(result.Note), FontSize = 11.5, Foreground = Views.Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            if (result.FixPreview != null && result.Fix != null)
                text.Children.Add(new TextBlock { Text = Plain(result.FixPreview), FontSize = 11.5, Foreground = PreviewBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (ActionButton(result) is { } action)
            {
                action.Margin = new Thickness(14, 2, 0, 0);
                Grid.SetColumn(action, 2);
                grid.Children.Add(action);
            }

            return new Border
            {
                Child = grid,
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10),
                CornerRadius = new CornerRadius(14),
                Background = CardBrush,
                BorderBrush = Tint(color, 0x4D),
                BorderThickness = new Thickness(1),
            };
        }

        private static Border PassedChip(HealthResult result)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Controls.Icon { Kind = "check", Width = 11, Height = 11, StrokeThickness = 3, Foreground = Views.Passed, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = Plain(result.Title), FontSize = 12, Foreground = ChipText, VerticalAlignment = VerticalAlignment.Center });
            string tip = Plain(result.Detail) + (result.Note != null ? "\n" + Plain(result.Note) : "");
            return new Border
            {
                Child = row,
                Height = 28,
                Padding = new Thickness(10, 0, 11, 0),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(14),
                Background = Tint(Views.Passed.Color, 0x14),
                BorderBrush = Tint(Views.Passed.Color, 0x2E),
                BorderThickness = new Thickness(1),
                ToolTip = tip.Trim().Length > 0 ? tip : null,
            };
        }

        private static SolidColorBrush Tint(Color color, byte alpha) => new(Color.FromArgb(alpha, color.R, color.G, color.B));

        // A passed check that offers an action, on one line: name, what was found, the action
        private FrameworkElement PassedRow(HealthResult result)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(Dot(result.Status, new Thickness(0, 5, 10, 0)));

            var title = new TextBlock { Text = Plain(result.Title), FontSize = 12, Foreground = PassedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 14, 0) };
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);

            var detail = new TextBlock { Text = Plain(result.Detail), FontSize = 12, Foreground = Views.Hint, TextWrapping = TextWrapping.Wrap };
            if (result.Note != null)
                detail.ToolTip = Plain(result.Note);
            Grid.SetColumn(detail, 2);
            grid.Children.Add(detail);

            if (ActionButton(result) is { } action)
            {
                action.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(action, 3);
                grid.Children.Add(action);
            }
            return grid;
        }

        private Button? ActionButton(HealthResult result)
        {
            Button button;
            if (result.Fix != null && result.FixLabel != null && result.Status != HealthStatus.Passed)
            {
                button = new Button { Content = WithIcon("wrench", Upper(Loc.T(result.FixLabel))), Style = (Style)FindResource("DiagButtonStyle") };
                button.ToolTip = result.FixPreview != null ? Plain(result.FixPreview) : null;
                button.Click += (_, _) => Views.ApplyFix(result);
            }
            else if (result.GuideTarget != null && result.GuideLabel != null)
            {
                string target = result.GuideTarget;
                button = SmallButton(Upper(Loc.T(result.GuideLabel)));
                if (!target.StartsWith("select:", StringComparison.Ordinal))
                    button.Content = WithIcon("external", Upper(Loc.T(result.GuideLabel)));
                button.Click += (_, _) => Views.Open(target);
            }
            else
            {
                return null;
            }
            button.VerticalAlignment = VerticalAlignment.Top;
            button.MinWidth = 64;
            return button;
        }

        private static Ellipse Dot(HealthStatus status, Thickness margin) => new()
        {
            Width = 8,
            Height = 8,
            Fill = Views.StatusBrush(status),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = margin,
            ToolTip = Views.StatusWord(status),
        };

        private void ShowCheckedAt() =>
            CheckedAt.Text = _checking ? Loc.T("Checking…")
                : AppState.HealthCheckedAt is { } at ? Loc.T("Last check: {0}", Views.Ago(at))
                : "";

        private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

        private async Task CheckAsync()
        {
            if (_checking)
                return;
            _checking = true;
            CheckButton.IsEnabled = false;
            ShowCheckedAt();
            try
            {
                await AppState.RefreshHealthAsync();
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Something went wrong: {0}", ex.Message), isError: true);
            }
            finally
            {
                _checking = false;
                CheckButton.IsEnabled = true;
                ShowResults();
            }
        }

        private async Task CheckIfNoneArrivesAsync()
        {
            for (int i = 0; i < 60 && AppState.HealthCheckedAt == null; i++)
                await Task.Delay(250);
            if (AppState.HealthCheckedAt == null)
                await CheckAsync();
        }

        private void FixAll_Click(object sender, RoutedEventArgs e)
        {
            var fixable = AppState.Health.Where(h => h.Status != HealthStatus.Passed && h.Fix != null).ToList();
            if (fixable.Count == 0)
                return;
            var real = fixable.Where(f => !f.Id.StartsWith("sample:")).ToList();
            if (real.Count == 0)
            {
                Views.Main.Toast(Loc.T("These are sample problems; nothing was changed."));
                return;
            }

            int before = BackupService.Load().Count;
            var failed = new List<string>();
            foreach (var result in real)
            {
                try { result.Fix!(); }
                catch (Exception ex) { failed.Add($"{result.Title}: {ex.Message}"); }
            }
            var made = BackupService.Load().Skip(before).ToList();
            if (failed.Count > 0)
            {
                Views.Main.Toast(Loc.T("Some fixes failed: {0}", string.Join("; ", failed)), isError: true);
            }
            else
            {
                Views.Main.Toast(Loc.N(real.Count, "Applied {0} fix.", "Applied {0} fixes."), made.Count == 0 ? null : () =>
                {
                    foreach (var entry in Enumerable.Reverse(made))
                        BackupService.Undo(entry);
                    _ = AppState.RefreshHealthAsync();
                    Views.Main.Toast(Loc.T("All fixes undone"));
                });
            }
            _ = AppState.RefreshHealthAsync();
        }

        // ── Fix history ──

        // Backups change in bursts (one entry per file), so the list is rebuilt once the burst is over
        private void QueueHistory()
        {
            _historyStale = true;
            if (_historyQueued)
                return;
            _historyQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _historyQueued = false;
                ShowHistory();
            }, DispatcherPriority.Background);
        }

        private void ShowHistory()
        {
            var entries = BackupService.Load().OrderByDescending(e => e.When).Take(60).ToList();
            TabHistory.Content = entries.Count > 0 ? Loc.T("FIX HISTORY ({0})", entries.Count) : Loc.T("FIX HISTORY");
            if (TabHistory.IsChecked != true)
            {
                _historyStale = true;
                return;
            }
            _historyStale = false;

            HistoryList.Children.Clear();
            if (entries.Count == 0)
            {
                HistoryList.Children.Add(Message(Loc.T("No fixes yet. Every change appears here with an Undo button.")));
                return;
            }

            var buttons = new List<(BackupEntry Entry, Button Undo)>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                grid.Children.Add(new TextBlock
                {
                    Text = Views.Ago(entry.When),
                    FontSize = 12,
                    Foreground = Views.Hint,
                    Margin = new Thickness(0, 1, 10, 0),
                    ToolTip = entry.When.ToString("g", Loc.Culture),
                });

                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = Plain(entry.Title), FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                if (entry.Detail.Length > 0)
                    text.Children.Add(new TextBlock { Text = Plain(entry.Detail), FontSize = 11.5, Foreground = Views.Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);

                var undo = SmallButton(Loc.T("UNDO"));
                undo.MinWidth = 64;
                undo.VerticalAlignment = VerticalAlignment.Top;
                undo.Margin = new Thickness(14, 0, 0, 0);
                undo.IsEnabled = false;
                undo.Click += (_, _) => Undo(entry);
                Grid.SetColumn(undo, 2);
                grid.Children.Add(undo);
                buttons.Add((entry, undo));

                HistoryList.Children.Add(new Border
                {
                    Child = grid,
                    Padding = new Thickness(0, 8, 0, 8),
                    BorderBrush = LineBrush,
                    BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                });
            }
            _ = ShowUndoableAsync(buttons, ++_historyVersion);
        }

        // Whether an entry can still be undone means reading the file it wrote, so that runs off the UI thread
        private async Task ShowUndoableAsync(List<(BackupEntry Entry, Button Undo)> buttons, int version)
        {
            var undoable = await Task.Run(() => buttons.Select(b => BackupService.CanUndo(b.Entry)).ToList());
            if (version != _historyVersion)
                return;
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].Undo.IsEnabled = undoable[i];
                buttons[i].Undo.ToolTip = undoable[i]
                    ? Loc.T("Put back what was there before")
                    : Loc.T("Changed again since, so this cannot be undone here");
                ToolTipService.SetShowOnDisabled(buttons[i].Undo, true);
            }
        }

        private void Undo(BackupEntry entry)
        {
            try
            {
                if (BackupService.Undo(entry))
                    Views.Main.Toast(Loc.T("Undone: {0}", Plain(entry.Title)));
                else
                    Views.Main.Toast(Loc.T("Changed again since, so this cannot be undone here"), isError: true);
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Something went wrong: {0}", ex.Message), isError: true);
            }
            _ = AppState.RefreshHealthAsync();
        }

        private void Backups_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(BackupService.Root);
            }
            catch { }
            Views.ShowInFolder(BackupService.Root);
        }

        // ── Views ──

        private void View_Checked(object sender, RoutedEventArgs e)
        {
            if (IsInitialized)
                ShowView();
        }

        private void ShowView()
        {
            bool history = TabHistory.IsChecked == true;
            ChecksView.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
            HistoryView.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
            BackupsButton.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
            FooterHint.Text = history
                ? Loc.T("Every change the launcher makes is backed up first. Undo puts back what was there before.")
                : Loc.T("Checks only read your files. Fixes run when you press a button, and each one can be undone.");
            if (history && _historyStale)
                ShowHistory();
        }

        private void Report_Click(object sender, RoutedEventArgs e) => Views.CopySupportReport();

        private Button SmallButton(string text) => new() { Content = text, Style = (Style)FindResource("SmallButtonStyle") };

        private static StackPanel WithIcon(string icon, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Controls.Icon { Kind = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0) });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        private static TextBlock Message(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = Views.Hint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        private static string Upper(string text) => text.ToUpperInvariant();

        // Check results are written in English; in right-to-left mode they are kept in their own reading order
        private static string Plain(string text) =>
            Loc.IsRightToLeft && !text.Any(c => c is >= '؀' and <= 'ۿ') ? Loc.Ltr(text) : text;
    }
}
