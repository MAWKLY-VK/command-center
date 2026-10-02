using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public partial class HomePage : UserControl, IPage
    {
        private ReplayEntry? _last;
        private bool _loading = true;

        public HomePage()
        {
            InitializeComponent();
            AppState.StatsChanged += ShowStats;
            AppState.HealthChanged += ShowHealth;
            AppState.ReplaysChanged += ShowLastMatch;
            BackupService.Changed += () => Dispatcher.Invoke(ShowChanges);

            SizeChanged += (_, _) => SideCol.Width = new GridLength(ActualWidth < 1150 ? 330 : 400);

            bool hasTest = GameLauncher.HasTestClient;
            ChannelBox.Visibility = hasTest ? Visibility.Visible : Visibility.Collapsed;
            (Views.Main.Channel == Channel.Live ? Live : Test).IsChecked = true;
            PlayOffline.IsEnabled = GameLauncher.HasOriginalGame;
            _loading = false;

            ShowChips();
            ShowStats();
            ShowHealth();
            ShowLastMatch();
            ShowChanges();
        }

        public string Title => "Command Center";
        public string Crumb => "Home";
        public string IconKey => "I.star";
        public FrameworkElement? TopRight => null;
        public FrameworkElement? Footer => null;

        public void OnShown()
        {
            ShowChips();
            ShowChanges();
        }

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 40 && (AppState.HealthCheckedAt == null || AppState.Stats == null); i++)
                await Task.Delay(250);
        }

        private void ShowChips()
        {
            Chips.Children.Clear();
            var settings = GoSettings.Load();
            string anticheat = settings.AntiCheat;
            string acName = anticheat.Equals("easyanticheat", StringComparison.OrdinalIgnoreCase) ? "Easy Anti-Cheat" : anticheat.Length > 0 ? "GO Anti-Cheat" : "No anti-cheat selected";
            bool eacReady = !anticheat.Equals("easyanticheat", StringComparison.OrdinalIgnoreCase) || AppState.Health.All(h => h.Id != "eac" || h.Status == HealthStatus.Passed);
            Chips.Children.Add(Views.Chip("shield", acName + (eacReady ? " ready" : " not installed"), eacReady ? Mark.Ok : Mark.Warn));
            Chips.Children.Add(Views.Chip("layers", "Client " + AppState.ClientVersion()));
            bool patch = settings.Get("data_packs", "use_community_data_patch", true);
            Chips.Children.Add(Views.Chip("cube", patch ? "Community patch on" : "Community patch off"));
            var res = OptionsFile.Get("Resolution")?.Replace(" ", " × ");
            var launcher = LauncherJson.Load();
            Chips.Children.Add(Views.Chip("monitor", launcher.Windowed ? $"Window {launcher.WindowedWidth} × {launcher.WindowedHeight}" : res ?? "Default resolution"));
        }

        private void ShowStats()
        {
            var stats = AppState.Stats;
            Players.Text = stats?.Players.ToString("N0") ?? "—";
            Lobbies.Text = stats?.Lobbies.ToString("N0") ?? "—";
            Latency.Text = stats?.LatencyMs.ToString() ?? "—";
            if (stats == null)
            {
                LatencyMark.Mark = Mark.Danger;
                LatencyText.Text = "Server not reachable";
            }
            else
            {
                // Time for the Generals Online service to answer; game traffic itself goes player to player
                (LatencyMark.Mark, LatencyText.Text) = stats.LatencyMs switch
                {
                    < 250 => (Mark.Ok, "Good"),
                    < 600 => (Mark.Warn, "Fair · other downloads may slow it"),
                    _ => (Mark.Danger, "Slow · check your connection"),
                };
            }
            ShowStatus();
        }

        private void ShowHealth()
        {
            var all = AppState.Health;
            int problems = AppState.ProblemCount, warnings = AppState.WarningCount, passed = all.Count - problems - warnings;
            Ring.Passed = passed;
            Ring.Warnings = warnings;
            Ring.Problems = problems;
            Score.Text = all.Count == 0 ? "…" : $"{passed}/{all.Count}";
            IssueCount.Text = all.Count == 0 ? "Checking" : problems + warnings == 0 ? "All clear" : problems + warnings == 1 ? "1 issue" : $"{problems + warnings} issues";
            Ui.SetSubtitle(HealthHead, AppState.HealthCheckedAt is { } at ? "Checked " + Views.Ago(at) : "Checking…");

            IssueList.Children.Clear();
            foreach (var issue in all.Where(h => h.Status != HealthStatus.Passed).OrderByDescending(h => h.Status).Take(3))
                IssueList.Children.Add(Views.HealthRow(issue, Views.ApplyFix, compact: true));
            if (problems + warnings > 3)
            {
                var more = new Button { Style = Views.Style("LinkBtn"), Content = $"See all {problems + warnings} in Health", Margin = new Thickness(27, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                more.Click += (_, _) => Views.Main.Navigate("health");
                IssueList.Children.Add(more);
            }
            HealthNote.Text = all.Count > 0 && problems + warnings == 0
                ? "Every check passed: game files, Windows, settings and network."
                : "Nothing changes until you press Fix. Every fix can be undone.";
            IssueList.Visibility = IssueList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowChips();
            ShowStatus();
        }

        private void ShowStatus()
        {
            if (!GamePaths.GameFound)
            {
                Status.Text = "Generals Online was not found. Start Command Center from the game folder, or pass --game with the folder.";
                PlayOnline.IsEnabled = false;
                return;
            }
            if (AppState.HealthCheckedAt == null)
            {
                Status.Text = "Checking your game, its settings and Windows…";
                return;
            }
            int issues = AppState.ProblemCount + AppState.WarningCount;
            string server = AppState.Stats != null ? $"{AppState.Stats.Players:N0} players are online." : "The Generals Online service is not answering right now.";
            Status.Text = issues == 0
                ? $"Your game passed every check. {server}"
                : $"Your game passed every check except {(issues == 1 ? "one" : issues.ToString())}. Fix {(issues == 1 ? "it" : "them")} on the right, then deploy. {server}";
        }

        private void ShowLastMatch()
        {
            _last = AppState.Replays.FirstOrDefault();
            MatchPlayers.Children.Clear();
            WatchMatch.IsEnabled = _last != null;
            if (_last == null)
            {
                MatchTitle.Text = "No matches yet";
                MatchInfo.Text = "Your replays appear here after your first game.";
                MatchMap.Source = null;
                MatchMapNone.Visibility = Visibility.Visible;
                return;
            }

            MatchKicker.Text = _last.IsArchived ? "Last match · archived automatically" : _last.IsLastMatch ? "Last match · not kept yet" : "Last match";
            MatchTitle.Text = _last.Map;
            var preview = Views.MapPreview(_last.MapPath, _last.Map);
            MatchMap.Source = preview;
            MatchMapNone.Visibility = preview == null ? Visibility.Visible : Visibility.Collapsed;

            var fighters = _last.Fighters.ToList();
            var teams = fighters.GroupBy(p => p.Team < 0 ? -100 - fighters.IndexOf(p) : p.Team).ToList();
            for (int t = 0; t < teams.Count && t < 4; t++)
            {
                if (t > 0)
                    MatchPlayers.Children.Add(new TrackedText { Text = "vs", FontSize = 13, Foreground = Views.Res("Text3"), Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
                foreach (var player in teams[t].Take(3))
                {
                    var tag = Views.Player(player, 130, 15);
                    tag.Margin = new Thickness(0, 0, 10, 0);
                    MatchPlayers.Children.Add(tag);
                }
                if (teams[t].Count() > 3)
                    MatchPlayers.Children.Add(Views.Tag($"+{teams[t].Count() - 3}"));
            }
            MatchInfo.Text = $"{Views.Length(_last.Length)}   ·   {Views.Day(_last.Start)}, {_last.Start:HH:mm}   ·   {_last.Mode}";
        }

        private void ShowChanges()
        {
            Changes.Children.Clear();
            var entries = BackupService.Load().OrderByDescending(e => e.When).Take(3).ToList();
            if (entries.Count == 0)
            {
                Changes.Children.Add(new TextBlock { Text = "No changes yet. Fixes, hotkeys and settings you save here are listed with an Undo button.", Style = Views.Style("Body"), FontSize = 13, Margin = new Thickness(20, 12, 20, 8) });
                return;
            }
            foreach (var entry in entries)
            {
                var row = new DockPanel { Margin = new Thickness(20, 10, 20, 6) };
                var undo = new Button { Content = "Undo", Style = Views.Style("BtnXs"), IsEnabled = BackupService.CanUndo(entry), VerticalAlignment = VerticalAlignment.Top };
                undo.ToolTip = undo.IsEnabled ? "Put back what was there before" : "The file changed again since, so this cannot be undone here";
                undo.Click += (_, _) =>
                {
                    if (BackupService.Undo(entry))
                        Views.Main.Toast("Undone: " + entry.Title);
                    _ = AppState.RefreshHealthAsync();
                };
                DockPanel.SetDock(undo, Dock.Right);
                row.Children.Add(undo);
                var when = new TextBlock { Text = Views.Ago(entry.When), Width = 74, Style = Views.Style("Small"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                row.Children.Add(when);
                var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                text.Children.Add(new TextBlock { Text = entry.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                if (entry.Detail.Length > 0)
                    text.Children.Add(new TextBlock { Text = entry.Detail, Style = Views.Style("Small"), Margin = new Thickness(0, 2, 0, 0) });
                row.Children.Add(text);
                Changes.Children.Add(row);
            }
        }

        private void PlayOnline_Click(object sender, RoutedEventArgs e) => _ = Views.Main.PlayAsync(online: true);
        private void PlayOffline_Click(object sender, RoutedEventArgs e) => _ = Views.Main.PlayAsync(online: false);

        private void Channel_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;
            var channel = Live.IsChecked == true ? Channel.Live : Channel.Test;
            Views.Main.Channel = channel;
            var launcher = LauncherJson.Load();
            launcher.PreferLive = channel == Channel.Live;
            try { launcher.Save(); } catch { }
        }

        private void Watch_Click(object sender, RoutedEventArgs e)
        {
            if (_last == null)
                return;
            try
            {
                ReplayService.Watch(_last);
            }
            catch (Exception ex)
            {
                Views.Main.Toast("Could not start the replay: " + ex.Message, isError: true);
            }
        }

        private void AllReplays_Click(object sender, RoutedEventArgs e) => Views.Main.Navigate("replays");

        private void Go_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string id })
                Views.Main.Navigate(id);
        }

        private void Report_Click(object sender, RoutedEventArgs e) => Views.CopySupportReport();
    }
}
