using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages.Tools
{
    // One replay in the list
    public sealed class ReplayRow : INotifyPropertyChanged
    {
        private static readonly Brush WonBrush = Views.Passed;
        private static readonly Brush LostBrush = Views.Brush("#FF6B6B");

        private BitmapSource? _preview;
        private ReplayResults? _results;
        private IReadOnlyList<PlayerChip> _chips;
        private FrameworkElement? _details;
        private readonly string _map;

        // installedMaps: folder names in the Maps folder, to show a map the way its maker wrote the name
        public ReplayRow(ReplayEntry entry, bool art, HashSet<string> installedMaps)
        {
            Entry = entry;
            _chips = MakeChips(art);
            _map = MapName(entry, installedMaps);
        }

        public ReplayEntry Entry { get; }

        public string Map => _map.Length > 0 ? Loc.Ltr(_map) : Loc.T("Unknown map");

        // Replays keep the map path in lower case: an installed map takes its folder's name, a map that comes
        // with the game gets capitals ("tournament desert" becomes "Tournament Desert")
        private static string MapName(ReplayEntry entry, HashSet<string> installedMaps)
        {
            if (entry.Map.Length == 0)
                return "";
            if (installedMaps.TryGetValue(entry.Map, out string? folder))
                return folder;
            if (entry.MapPath.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(entry.Map);
            return entry.Map;
        }

        public string Sub
        {
            get
            {
                string mode = ModeText(Entry.Mode);
                if (Entry.IsLastMatch)
                    return mode + " · " + Loc.T("last match, not kept yet");
                return Entry.IsArchived ? mode + " · " + Loc.T("archived") : mode;
            }
        }

        public Brush SubBrush => Entry.IsLastMatch ? Views.Warning : Views.Hint;
        public string Day => Views.Day(Entry.Start);
        public string Time => Entry.Start.ToString("HH:mm");
        public string Length => Loc.Ltr(Views.Length(Entry.Length));

        public string PlayersTip => string.Join("\n", Entry.Players.Select(p => $"{PlayerName(p)} · {Army(p)}"));

        public IReadOnlyList<PlayerChip> Chips
        {
            get => _chips;
            private set
            {
                _chips = value;
                Raise(nameof(Chips));
            }
        }

        public BitmapSource? Preview
        {
            get => _preview;
            set
            {
                _preview = value;
                Raise(nameof(Preview));
            }
        }

        // Read from the recorded commands in the background; null until then
        public ReplayResults? Results
        {
            get => _results;
            set
            {
                _results = value;
                Raise(nameof(ResultText));
                Raise(nameof(ResultBrush));
                Raise(nameof(ResultTip));
            }
        }

        // The expanded view of the selected replay, null for the others
        public FrameworkElement? Details
        {
            get => _details;
            set
            {
                _details = value;
                Raise(nameof(Details));
            }
        }

        public ReplayPlayerResult? ResultOf(int index) =>
            _results != null && index >= 0 && index < _results.Players.Count ? _results.Players[index] : null;

        public int Recorder => _results?.Recorder ?? -1;

        // The recorder's own result when it is known, otherwise who won, otherwise a dash
        public string ResultText
        {
            get
            {
                if (_results == null)
                    return "";
                switch (ResultOf(_results.Recorder)?.Result)
                {
                    case MatchResult.Won: return Loc.T("Won");
                    case MatchResult.Lost: return Loc.T("Lost");
                }
                var winners = Entry.Players.Where((p, i) => ResultOf(i)?.Result == MatchResult.Won).ToList();
                if (winners.Count > 0)
                    return Loc.T("{0} won", PlayerName(winners[0]) + (winners.Count > 1 ? $" +{winners.Count - 1}" : ""));
                return "—";
            }
        }

        public Brush ResultBrush => ResultOf(_results?.Recorder ?? -1)?.Result switch
        {
            MatchResult.Won => WonBrush,
            MatchResult.Lost => LostBrush,
            _ => Views.Hint,
        };

        public string? ResultTip
        {
            get
            {
                if (_results == null)
                    return null;
                var lines = Entry.Players.Select((p, i) => (p, r: ResultOf(i)))
                    .Where(x => x.r?.HasLeft == true)
                    .Select(x => $"{PlayerName(x.p)} · {LeaveText(x.r!)}")
                    .ToList();
                if (!_results.HasWinner)
                    lines.Add(Loc.T("A replay only shows who won when the other side surrendered or left; a defeat in the game is not recorded."));
                return string.Join("\n", lines);
            }
        }

        // Faction badges come from the game's own pictures once they are read
        public void ShowArt() => Chips = MakeChips(art: true);

        private List<PlayerChip> MakeChips(bool art)
        {
            var chips = new List<PlayerChip>();
            int number = 0;
            foreach (var side in Sides(Entry))
            {
                foreach (var (p, _) in side)
                    chips.Add(new PlayerChip(PlayerName(p), Views.PlayerBrush(p.Color), art ? Emblem(p) : null, number));
                number++;
            }
            return chips;
        }

        // Players who fight, side by side: a team, or a player on their own; in the order of the game's slots
        public static List<IGrouping<int, (ReplayPlayer Player, int Index)>> Sides(ReplayEntry entry) =>
            entry.Players.Select((p, i) => (Player: p, Index: i))
                .Where(x => x.Player.Faction != Faction.Observer)
                .GroupBy(x => x.Player.Team >= 0 ? x.Player.Team : -1 - x.Index)
                .ToList();

        public static BitmapSource? Emblem(ReplayPlayer player) =>
            FactionArt.Emblem(player.Faction, player.General, Views.PlayerBrush(player.Color) is SolidColorBrush b ? b.Color : Colors.White);

        public static string PlayerName(ReplayPlayer player) => player.IsHuman ? Loc.Ltr(player.Name) : Loc.T(player.Name);

        public static string Army(ReplayPlayer player) => player.Faction switch
        {
            Faction.Random => Loc.T("Random"),
            Faction.Observer => Loc.T("Observer"),
            _ when player.FactionName.Length == 0 => Loc.T(player.General),
            _ => player.General.Length > 0 ? Loc.T(player.FactionName) + " · " + Loc.T(player.General) : Loc.T(player.FactionName),
        };

        public static string LeaveText(ReplayPlayerResult result)
        {
            if (!result.HasLeft)
                return "";
            string at = result.LeftAt is { } time ? Loc.Ltr(ReplayPlayerResult.Clock(time)) : "";
            return result.Leave switch
            {
                LeaveKind.Surrendered => at.Length > 0 ? Loc.T("Surrendered at {0}", at) : Loc.T("Surrendered"),
                LeaveKind.Left => at.Length > 0 ? Loc.T("Left at {0}", at) : Loc.T("Left the game"),
                LeaveKind.Disconnected => at.Length > 0 ? Loc.T("Disconnected at {0}", at) : Loc.T("Disconnected"),
                _ => at.Length > 0 ? Loc.T("Left or surrendered at {0}", at) : Loc.T("Left or surrendered"),
            };
        }

        public static string ModeText(string mode)
        {
            if (mode == "Skirmish" || mode == "Solo")
                return Loc.T(mode);
            if (mode.StartsWith("FFA ", StringComparison.Ordinal))
                return Loc.T("FFA {0}", mode[4..]);
            return Loc.Ltr(mode);
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // Replays: every match from Replays and ArchivedReplays with map, players, date, length and result. The selected
    // replay opens up with its teams, who left and when, and the file actions; Watch starts the game in replay mode.
    public partial class ReplaysSection : UserControl, IToolSection
    {
        private static readonly Brush LostBrush = Views.Brush("#FF6B6B");

        private readonly ObservableCollection<ReplayRow> _rows = new();
        private readonly ListCollectionView _view;
        private ReplayRow? _open;          // the row whose details are shown
        private string? _selectPath;       // select this file after the next refresh (renamed or kept)
        private bool _listed, _artReady, _renaming;
        private int _reading;

        public ReplaysSection()
        {
            InitializeComponent();
            RecycleUndo.CleanUp();
            _view = new ListCollectionView(_rows) { Filter = Matches };
            List.ItemsSource = _view;
            AppState.ReplaysChanged += () => Dispatcher.BeginInvoke(() =>
            {
                _listed = true;
                Fill();
            });
            Fill();
            _ = LoadArtAsync();
        }

        private ReplayRow? Selected => List.SelectedItem as ReplayRow;

        public bool HasPendingChanges => false;

        public void OnShown()
        {
            Archive.IsChecked = ReplayService.ArchiveEnabled;
            _ = AppState.RefreshReplaysAsync();
        }

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 80 && !_listed; i++)
                await Task.Delay(250);
            for (int i = 0; i < 80 && (_reading > 0 || !_artReady); i++)
                await Task.Delay(250);
            await Task.Delay(400);
        }

        public void ShowPart(string part) { }

        private async Task LoadArtAsync()
        {
            await FactionArt.PreloadAsync();
            _artReady = true;
            foreach (var row in _rows)
                row.ShowArt();
            RefreshDetails();
        }

        // ── List ──

        private void Fill()
        {
            var previous = Selected;
            int index = previous != null ? _view.IndexOf(previous) : 0;
            string? path = _selectPath ?? previous?.Entry.FilePath;
            _selectPath = null;
            CloseDetails();

            _rows.Clear();
            var installedMaps = MapCatalog.InstalledFolders();
            foreach (var entry in AppState.Replays)
                _rows.Add(new ReplayRow(entry, _artReady, installedMaps));
            UpdateEmpty();

            var again = _rows.FirstOrDefault(r => string.Equals(r.Entry.FilePath, path, StringComparison.OrdinalIgnoreCase) && _view.Contains(r));
            if (again == null && !_view.IsEmpty)
                again = _view.GetItemAt(Math.Clamp(index, 0, _view.Count - 1)) as ReplayRow;
            List.SelectedItem = again;
            UpdateBar();

            // Map previews and results are read off the UI thread
            var rows = _rows.ToList();
            if (rows.Count == 0)
                return;
            _reading++;
            Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    var preview = Views.MapPreview(row.Entry.MapPath, row.Entry.Map);
                    var results = ReplayService.ReadResults(row.Entry);
                    Dispatcher.BeginInvoke(() =>
                    {
                        row.Preview = preview;
                        row.Results = results;
                        if (row == _open)
                            RefreshDetails();
                    });
                }
            }).ContinueWith(_ => Dispatcher.BeginInvoke(() => _reading--));
        }

        private bool Matches(object item)
        {
            if (item is not ReplayRow row)
                return false;
            string q = Search?.Text.Trim() ?? "";
            return q.Length == 0
                   || row.Entry.Map.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || row.Entry.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || row.Entry.Players.Any(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view.Refresh();
            if (Selected == null && !_view.IsEmpty)
                List.SelectedIndex = 0;
            UpdateEmpty();
            UpdateBar();
        }

        private void UpdateEmpty()
        {
            Empty.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count == 0)
            {
                EmptyTitle.Text = _listed ? Loc.T("No replays yet") : Loc.T("Loading…");
                EmptyText.Text = _listed ? Loc.T("Play a match and it appears here.") : "";
            }
            else
            {
                EmptyTitle.Text = Loc.T("Nothing matches");
                EmptyText.Text = Loc.T("Try another map or player name.");
            }
        }

        private void UpdateBar()
        {
            var row = Selected;
            WatchButton.IsEnabled = row != null;
            KeepButton.Visibility = row?.Entry.IsLastMatch == true ? Visibility.Visible : Visibility.Collapsed;
            string count = _view.Count == _rows.Count
                ? Loc.N(_rows.Count, "{0} replay", "{0} replays")
                : Loc.T("{0} of {1} replays", _view.Count, _rows.Count);
            Status.Text = _rows.Count == 0 ? "" : count + " · " + Loc.T("Enter watches, F2 renames, Del deletes");
        }

        // The column titles follow the rows when the scroll bar takes room on the side
        private void List_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.OriginalSource is ScrollViewer viewer)
                Header.Margin = new Thickness(14, 0, viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 26 : 14, 0);
        }

        // ── Details of the selected replay ──

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = Selected;
            if (_open != row)
            {
                CloseDetails();
                _open = row;
                if (row != null)
                {
                    row.Details = BuildDetails(row);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (Selected == row)
                            List.ScrollIntoView(row);
                    }, DispatcherPriority.Loaded);
                }
            }
            UpdateBar();
        }

        private void CloseDetails()
        {
            if (_open != null)
                _open.Details = null;
            _open = null;
            _renaming = false;
        }

        private void RefreshDetails()
        {
            if (_open != null && !_renaming)
                _open.Details = BuildDetails(_open);
        }

        private FrameworkElement BuildDetails(ReplayRow row)
        {
            var r = row.Entry;
            var root = new StackPanel();

            // Sides in two columns; a team game names its teams and their result
            var sides = ReplayRow.Sides(r);
            bool teams = sides.Any(s => s.Count() > 1);
            var grid = new System.Windows.Controls.Primitives.UniformGrid
            {
                Columns = sides.Count > 1 ? 2 : 1,
                MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            foreach (var side in sides)
            {
                var box = new StackPanel { Margin = new Thickness(0, 0, 16, 4) };
                if (teams)
                {
                    var result = row.ResultOf(side.First().Index)?.Result;
                    string label = side.Key >= 0 ? Loc.T("TEAM {0}", side.Key + 1) : Loc.T("ON THEIR OWN");
                    if (result != null)
                        label += " · " + (result == MatchResult.Won ? Loc.T("WON") : Loc.T("LOST"));
                    box.Children.Add(new TextBlock
                    {
                        Text = label,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = result == MatchResult.Won ? Views.Passed : result == MatchResult.Lost ? LostBrush : Views.Hint,
                        Margin = new Thickness(0, 0, 0, 6),
                    });
                }
                foreach (var (player, index) in side)
                    box.Children.Add(PlayerLine(player, row.ResultOf(index), showResult: !teams));
                grid.Children.Add(box);
            }
            root.Children.Add(grid);

            var observers = r.Players.Where(p => p.Faction == Faction.Observer).Select(ReplayRow.PlayerName).ToList();
            if (observers.Count > 0)
                root.Children.Add(Line(Loc.T("Watched by {0}", Loc.List(observers)), Views.Hint));
            if (r.Desync)
                root.Children.Add(Line(Loc.T("Players fell out of sync in this match; the replay may stop early."), Views.Warning));
            if (r.QuitEarly)
                root.Children.Add(Line(Loc.T("Recorded by a player who left before the end."), Views.Hint));

            // File and match settings
            string folder = r.IsArchived ? GamePaths.ArchivedReplays : GamePaths.Replays;
            var info = new List<string> { Loc.Ltr((r.IsArchived ? @"ArchivedReplays\" : @"Replays\") + Path.GetRelativePath(folder, r.FilePath)) };
            info.Add(Loc.Ltr(Views.Size(r.SizeBytes)));
            if (r.Version.Length > 0)
                info.Add(Loc.T("Version {0}", Loc.Ltr(r.Version)));
            if (r.StartingCash > 0)
                info.Add(Loc.T("Starting cash {0}", Loc.Ltr($"${r.StartingCash:N0}")));
            if (r.SuperweaponsOff)
                info.Add(Loc.T("Superweapons off"));
            if (row.Recorder >= 0 && row.Recorder < r.Players.Count)
                info.Add(Loc.T("Recorded by {0}", ReplayRow.PlayerName(r.Players[row.Recorder])));
            var file = Line(string.Join("  ·  ", info), Views.Hint);
            file.ToolTip = r.FilePath;
            root.Children.Add(file);

            // Rename in place, or the file actions
            var renameBox = new TextBox { Style = (Style)FindResource("TextBoxStyle"), Width = double.NaN, Height = 26, FontSize = 12, Padding = new Thickness(6, 0, 6, 0) };
            var save = SmallButton(Loc.T("SAVE"), null);
            var cancel = SmallButton(Loc.T("CANCEL"), null);
            var rename = new DockPanel { Margin = new Thickness(0, 8, 0, 2), Visibility = Visibility.Collapsed, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            DockPanel.SetDock(cancel, Dock.Right);
            DockPanel.SetDock(save, Dock.Right);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            save.Margin = new Thickness(8, 0, 0, 0);
            rename.Children.Add(cancel);
            rename.Children.Add(save);
            rename.Children.Add(renameBox);

            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 2) };
            var renameButton = SmallButton(Loc.T("RENAME"), null, "F2");
            var copyButton = SmallButton(Loc.T("COPY FILE"), null, Loc.T("Copies the replay so you can paste it into Discord or a folder"));
            var showButton = SmallButton(Loc.T("SHOW IN FOLDER"), null);
            var deleteButton = SmallButton(Loc.T("DELETE"), Views.Problem, Loc.T("Moves the replay to the Recycle Bin; Undo brings it back (Del)"));
            foreach (var button in new[] { renameButton, copyButton, showButton, deleteButton })
            {
                button.Margin = new Thickness(0, 0, 8, 6);
                actions.Children.Add(button);
            }
            root.Children.Add(rename);
            root.Children.Add(actions);

            void EndRename()
            {
                _renaming = false;
                rename.Visibility = Visibility.Collapsed;
                actions.Visibility = Visibility.Visible;
                List.Focus();
            }
            void StartRename()
            {
                _renaming = true;
                renameBox.Text = Path.GetFileNameWithoutExtension(r.FileName);
                actions.Visibility = Visibility.Collapsed;
                rename.Visibility = Visibility.Visible;
                renameBox.Focus();
                renameBox.SelectAll();
            }
            void CommitRename()
            {
                if (Rename(row, renameBox.Text))
                    EndRename();
            }
            renameButton.Click += (_, _) => StartRename();
            save.Click += (_, _) => CommitRename();
            cancel.Click += (_, _) => EndRename();
            renameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitRename();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    EndRename();
                    e.Handled = true;
                }
            };
            copyButton.Click += (_, _) => Copy(row);
            showButton.Click += (_, _) => Views.ShowInFolder(r.FilePath);
            deleteButton.Click += (_, _) => Delete(row);
            root.Tag = (Action)StartRename;
            return root;
        }

        // Badge in the player's colour, name and result; army and when the player left below
        private FrameworkElement PlayerLine(ReplayPlayer player, ReplayPlayerResult? result, bool showResult)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            FrameworkElement badge = _artReady && ReplayRow.Emblem(player) is { } emblem
                ? new Image { Source = emblem, Width = 20, Height = 20, FlowDirection = FlowDirection.LeftToRight }
                : new Border { Width = 10, Height = 10, Margin = new Thickness(5, 0, 5, 0), Background = Views.PlayerBrush(player.Color) };
            RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.Fant);
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(badge.Margin.Left, 2, 8, 0);
            DockPanel.SetDock(badge, Dock.Left);
            line.Children.Add(badge);

            var text = new StackPanel();
            // Name, then the result right after it
            var top = new DockPanel { LastChildFill = false };
            var name = new TextBlock
            {
                Text = ReplayRow.PlayerName(player),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = 210,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = player.Name,
            };
            DockPanel.SetDock(name, Dock.Left);
            top.Children.Add(name);
            if (showResult && result?.Result is { } outcome)
            {
                var tag = new TextBlock
                {
                    Text = outcome == MatchResult.Won ? Loc.T("WON") : Loc.T("LOST"),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = outcome == MatchResult.Won ? Views.Passed : LostBrush,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(tag, Dock.Left);
                top.Children.Add(tag);
            }
            text.Children.Add(top);

            var sub = new TextBlock { FontSize = 11, Foreground = Views.Hint, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            sub.Inlines.Add(new Run(ReplayRow.Army(player)));
            if (result?.HasLeft == true)
            {
                sub.Inlines.Add(new Run("  ·  "));
                sub.Inlines.Add(new Run(ReplayRow.LeaveText(result)) { Foreground = result.Leave == LeaveKind.Disconnected ? Views.Problem : Views.Warning });
            }
            text.Children.Add(sub);
            line.Children.Add(text);
            return line;
        }

        private static TextBlock Line(string text, Brush brush) => new()
        {
            Text = text,
            FontSize = 11,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 4),
        };

        private Button SmallButton(string text, Brush? border, string? tip = null)
        {
            var button = new Button { Content = text, Style = (Style)FindResource("SmallButtonStyle"), ToolTip = tip };
            if (border != null)
                button.BorderBrush = border;
            return button;
        }

        // ── Actions ──

        private void Watch_Click(object sender, RoutedEventArgs e) => Watch();

        private void Watch()
        {
            if (Selected is not { } row)
                return;
            if (GameLauncher.IsGameRunning())
            {
                Views.Main.Toast(Loc.T("Close the game first, then watch the replay."), isError: true);
                return;
            }
            try
            {
                ReplayService.Watch(row.Entry);
                Views.Main.Toast(Loc.T("Starting the replay of {0}…", row.Map));
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Could not start the replay: {0}", ex.Message), isError: true);
            }
        }

        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && (FindParent<ButtonBase>(d) != null || FindParent<TextBox>(d) != null || FindParent<ListBoxItem>(d) == null))
                return;
            Watch();
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox || Selected is not { } row)
                return;
            if (e.Key == Key.Enter)
            {
                Watch();
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && row.Details?.Tag is Action startRename)
            {
                startRename();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                Delete(row);
                e.Handled = true;
            }
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            for (var d = child; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                if (d is T found)
                    return found;
            }
            return null;
        }

        // The game always writes the last match to 00000000.rep and overwrites it next time
        private void Keep_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } row)
                return;
            try
            {
                string kept = ReplayService.Keep(row.Entry);
                _selectPath = kept;
                Views.Main.Toast(Loc.T("Kept as {0}", Loc.Ltr(Path.GetFileName(kept))));
                _ = AppState.RefreshReplaysAsync();
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Could not keep it: {0}", ex.Message), isError: true);
            }
        }

        private bool Rename(ReplayRow row, string name)
        {
            string old = row.Entry.FilePath;
            if (name.Trim() == Path.GetFileNameWithoutExtension(old))
                return true;
            try
            {
                string renamed = ReplayService.Rename(row.Entry, name);
                _selectPath = renamed;
                Views.Main.Toast(Loc.T("Renamed to {0}", Loc.Ltr(Path.GetFileName(renamed))), () =>
                {
                    try
                    {
                        File.Move(renamed, old);
                        _selectPath = old;
                    }
                    catch (Exception ex)
                    {
                        Views.Main.Toast(ex.Message, isError: true);
                    }
                    _ = AppState.RefreshReplaysAsync();
                });
                _ = AppState.RefreshReplaysAsync();
                return true;
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Could not rename it: {0}", ex.Message), isError: true);
                return false;
            }
        }

        private static void Copy(ReplayRow row)
        {
            try
            {
                Clipboard.SetFileDropList(new StringCollection { row.Entry.FilePath });
                Views.Main.Toast(Loc.T("Replay copied. Paste it into Discord or a folder."));
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Could not copy: {0}", ex.Message), isError: true);
            }
        }

        // The file goes to the Recycle Bin, so Windows can also restore it later
        private void Delete(ReplayRow row)
        {
            var undo = RecycleUndo.Recycle(new[] { row.Entry.FilePath }, out var removed, out var failed);
            if (removed.Count == 0)
            {
                Views.Main.Toast(Loc.T("Could not delete: {0}", string.Join("; ", failed)), isError: true);
                return;
            }
            Views.Main.Toast(Loc.T("Moved {0} to the Recycle Bin.", Loc.Ltr(row.Entry.FileName)), () =>
            {
                try
                {
                    undo();
                    _selectPath = row.Entry.FilePath;
                    Views.Main.Toast(Loc.T("Change undone"));
                }
                catch (Exception ex)
                {
                    Views.Main.Toast(Loc.T("Could not put it back: {0}", ex.Message), isError: true);
                }
                _ = AppState.RefreshReplaysAsync();
            });
            _ = AppState.RefreshReplaysAsync();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Directory.CreateDirectory(GamePaths.Replays); } catch { }
            Views.ShowInFolder(GamePaths.Replays);
        }

        // The game's own option: with ArchiveReplays on it copies every match to ArchivedReplays
        private void Archive_Click(object sender, RoutedEventArgs e)
        {
            bool on = Archive.IsChecked == true;
            try
            {
                ReplayService.SetArchive(on);
                Views.Main.Toast(on ? Loc.T("Every match is now archived by the game.") : Loc.T("Match archive turned off."), () =>
                {
                    try
                    {
                        ReplayService.SetArchive(!on);
                        Archive.IsChecked = !on;
                    }
                    catch (Exception ex)
                    {
                        Views.Main.Toast(Loc.T("Could not change Options.ini: {0}", ex.Message), isError: true);
                    }
                });
            }
            catch (Exception ex)
            {
                Archive.IsChecked = !on;
                Views.Main.Toast(Loc.T("Could not change Options.ini: {0}", ex.Message), isError: true);
            }
        }
    }
}
