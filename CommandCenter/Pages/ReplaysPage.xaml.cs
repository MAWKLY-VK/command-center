using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public sealed record PlayerChipView(string Name, Brush Brush, string Tip);

    public sealed class ReplayRow : INotifyPropertyChanged
    {
        private bool _starred;
        private BitmapSource? _preview;

        public ReplayRow(ReplayEntry entry, bool starred)
        {
            Entry = entry;
            _starred = starred;
            var fighters = entry.Fighters.ToList();
            var chips = fighters.Select(p => new PlayerChipView(p.Name, Views.PlayerBrush(p.Color), $"{p.Name} · {p.Army}")).ToList();
            Shown = chips.Take(2).ToList();
            int more = chips.Count - Shown.Count;
            More = more > 0 ? $"+{more}" : "";
            AllPlayers = string.Join("\n", chips.Select(c => c.Tip));
        }

        public ReplayEntry Entry { get; }
        public string Map => Entry.Map.Length > 0 ? Entry.Map : "Unknown map";
        public string FileName => Entry.FileName;
        public string Sub => $"{Entry.Start:HH:mm} · " + (Entry.IsLastMatch ? "last match, not kept" : Entry.IsArchived ? "archived" : Path.GetFileNameWithoutExtension(Entry.FileName));
        public List<PlayerChipView> Shown { get; }
        public string More { get; }
        public Visibility MoreVisibility => More.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string AllPlayers { get; }
        public string Mode => Entry.Mode;
        public string Length => Views.Length(Entry.Length);
        public string Size => Views.Size(Entry.SizeBytes);

        public string Group
        {
            get
            {
                var day = Entry.Start.Date;
                if (day == DateTime.Today) return "Today";
                if (day == DateTime.Today.AddDays(-1)) return "Yesterday";
                if (day > DateTime.Today.AddDays(-7)) return "This week";
                return Entry.Start.ToString("MMMM yyyy");
            }
        }

        public BitmapSource? Preview
        {
            get => _preview;
            set { _preview = value; PropertyChanged?.Invoke(this, new(nameof(Preview))); }
        }

        public bool Starred
        {
            get => _starred;
            set
            {
                if (_starred == value) return;
                _starred = value;
                PropertyChanged?.Invoke(this, new(nameof(Starred)));
                StarChanged?.Invoke(this);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public static event Action<ReplayRow>? StarChanged;
    }

    public partial class ReplaysPage : UserControl, IPage
    {
        private readonly ObservableCollection<ReplayRow> _rows = new();
        private readonly ListCollectionView _view;
        private readonly HashSet<string> _starred;
        private readonly DockPanel _footer;
        private readonly Border _topRight;
        private bool _loaded;

        private static string StarFile => Path.Combine(GamePaths.AppData, "starred.json");
        private static string TrashFolder => Path.Combine(GamePaths.AppData, "Deleted replays");

        public ReplaysPage()
        {
            InitializeComponent();
            _starred = LoadStars();
            _view = new ListCollectionView(_rows);
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ReplayRow.Group)));
            _view.Filter = Matches;
            List.ItemsSource = _view;
            ReplayRow.StarChanged += SaveStar;

            _topRight = Views.Chip("folder", "Replays · ArchivedReplays");
            _topRight.Cursor = Cursors.Hand;
            _topRight.ToolTip = GamePaths.Replays;
            _topRight.MouseLeftButtonUp += (_, _) => Views.ShowInFolder(GamePaths.Replays);

            _footer = new DockPanel();
            var open = new Button { Content = "Open replays folder", Style = Views.Style("BtnLg") };
            Ui.SetIcon(open, Views.Icon("folder"));
            open.Click += (_, _) => Views.ShowInFolder(GamePaths.Replays);
            DockPanel.SetDock(open, Dock.Right);
            _footer.Children.Add(open);
            var hint = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            hint.Children.Add(new Controls.Icon { Data = Views.Icon("replay"), Width = 17, Height = 17, Foreground = Views.Res("IconIdle"), Margin = new Thickness(0, 0, 10, 0) });
            hint.Children.Add(new TextBlock { Text = "Watch starts the game in replay mode. Enter watches, F2 renames, Del deletes (with Undo).", Foreground = Views.Res("Text2"), VerticalAlignment = VerticalAlignment.Center });
            _footer.Children.Add(hint);

            AppState.ReplaysChanged += Fill;
            Fill();
        }

        public string Title => "Replays";
        public string Crumb => "Match archive";
        public string IconKey => "I.replay";
        public FrameworkElement? TopRight => _topRight;
        public FrameworkElement? Footer => _footer;

        public void OnShown()
        {
            Archive.IsChecked = ReplayService.ArchiveEnabled;
            if (_loaded)
                _ = AppState.RefreshReplaysAsync();
            _loaded = true;
        }

        public void FocusSearch()
        {
            Search.Focus();
            Search.SelectAll();
        }

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 20 && _rows.Count == 0; i++)
                await Task.Delay(200);
            await Task.Delay(400);
        }

        private void Fill()
        {
            string? selected = (List.SelectedItem as ReplayRow)?.Entry.FilePath;
            _rows.Clear();
            foreach (var entry in AppState.Replays)
                _rows.Add(new ReplayRow(entry, _starred.Contains(entry.FilePath)));

            TabAll.SetValue(Ui.BadgeProperty, _rows.Count.ToString());
            TabStarred.SetValue(Ui.BadgeProperty, _rows.Count(r => r.Starred).ToString());
            TabArchived.SetValue(Ui.BadgeProperty, _rows.Count(r => r.Entry.IsArchived).ToString());
            UpdateEmpty();

            var again = _rows.FirstOrDefault(r => r.Entry.FilePath == selected) ?? _rows.FirstOrDefault(r => _view.Contains(r));
            List.SelectedItem = again;
            if (again == null)
                ShowDetails(null);

            // Previews are decoded off the UI thread
            var rows = _rows.ToList();
            _ = Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    var preview = Views.MapPreview(row.Entry.MapPath, row.Entry.Map);
                    Dispatcher.BeginInvoke(() => row.Preview = preview);
                }
            });
        }

        private bool Matches(object item)
        {
            if (item is not ReplayRow row)
                return false;
            if (TabStarred?.IsChecked == true && !row.Starred) return false;
            if (TabArchived?.IsChecked == true && !row.Entry.IsArchived) return false;
            string q = Search?.Text.Trim() ?? "";
            if (q.Length == 0)
                return true;
            return row.Map.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || row.Entry.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || row.Entry.Players.Any(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateEmpty()
        {
            bool empty = _view.IsEmpty;
            Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = _rows.Count == 0 ? "No replays yet. Play a match and it appears here." : "Nothing matches this filter";
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            _view.Refresh();
            UpdateEmpty();
        }

        private void Search_Changed(object sender, TextChangedEventArgs e) => Filter_Changed(sender, e);

        private ReplayRow? Selected => List.SelectedItem as ReplayRow;

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(Selected);

        private void ShowDetails(ReplayRow? row)
        {
            Details.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
            NoSelection.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
            if (row == null)
                return;
            var r = row.Entry;
            Banner.Source = Views.MapPreview(r.MapPath, r.Map);
            When.Text = $"{Views.Day(r.Start)} · {r.Start:HH:mm}";
            DetailTitle.Text = row.Map;
            DetailTitle.ToolTip = row.Map;
            DLength.Text = Views.Length(r.Length);
            DVersion.Text = r.Version.Length > 0 ? r.Version : "Unknown";
            DCash.Text = r.StartingCash > 0 ? $"${r.StartingCash:N0}" : "Default";
            DSuper.Text = r.SuperweaponsOff ? "Off" : "On";
            DFile.Text = (r.IsArchived ? @"ArchivedReplays\" : @"Replays\") + Path.GetRelativePath(r.IsArchived ? GamePaths.ArchivedReplays : GamePaths.Replays, r.FilePath);
            DFile.ToolTip = r.FilePath;
            KeepBtn.Visibility = r.IsLastMatch ? Visibility.Visible : Visibility.Collapsed;

            Teams.Children.Clear();
            var fighters = r.Fighters.ToList();
            var teams = fighters.GroupBy(p => p.Team < 0 ? -100 - fighters.IndexOf(p) : p.Team).ToList();
            var grid = new UniformGrid { Columns = Math.Min(2, Math.Max(1, teams.Count)) };
            int n = 1;
            foreach (var team in teams)
            {
                var box = new StackPanel { Margin = new Thickness(0, 0, 8, 10) };
                box.Children.Add(new TrackedText { Style = Views.Style("Lbl") as Style, Text = teams.Count > 1 ? (team.Key >= 0 ? $"Team {team.Key + 1}" : $"Player {n}") : "Player", Margin = new Thickness(0, 0, 0, 6) });
                foreach (var p in team)
                {
                    var tile = new Chamfer { Style = Views.Style("Tile"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6) };
                    var stack = new StackPanel();
                    stack.Children.Add(Views.Player(p, 140, 14.5));
                    stack.Children.Add(new TextBlock { Text = p.Army, Style = Views.Style("Small"), Margin = new Thickness(18, 2, 0, 0) });
                    tile.Child = stack;
                    box.Children.Add(tile);
                }
                grid.Children.Add(box);
                n++;
            }
            Teams.Children.Add(grid);
            var observers = r.Players.Where(p => p.Faction == Faction.Observer).Select(p => p.Name).ToList();
            if (observers.Count > 0)
                Teams.Children.Add(new TextBlock { Text = "Observers: " + string.Join(", ", observers), Style = Views.Style("Small"), TextWrapping = TextWrapping.Wrap });

            Flags.Children.Clear();
            if (r.Desync)
                Flags.Children.Add(Flag(Mark.Warn, "Players fell out of sync in this match; the replay may stop early."));
            if (r.QuitEarly)
                Flags.Children.Add(Flag(Mark.Info, "Recorded by a player who left before the end."));
        }

        private static FrameworkElement Flag(Mark mark, string text)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var m = new StatusMark { Mark = mark, Width = 13, Height = 13, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(m, Dock.Left);
            dock.Children.Add(m);
            dock.Children.Add(new TextBlock { Text = text, Style = Views.Style("Body"), FontSize = 13, LineHeight = 18 });
            return dock;
        }

        // ── Actions ──

        private void Watch_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } row)
                return;
            if (GameLauncher.IsGameRunning())
            {
                Views.Main.Toast("Close the game first, then watch the replay.", isError: true);
                return;
            }
            try
            {
                ReplayService.Watch(row.Entry);
                Views.Main.Toast($"Starting the replay of {row.Map}…");
            }
            catch (Exception ex)
            {
                Views.Main.Toast("Could not start the replay: " + ex.Message, isError: true);
            }
        }

        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ToggleButton>(d) != null)
                return;
            Watch_Click(sender, e);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Watch_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.F2) { Rename_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Delete) { Delete_Click(sender, e); e.Handled = true; }
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            for (var d = child; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is T t) return t;
            return null;
        }

        private void Keep_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } row)
                return;
            try
            {
                string kept = ReplayService.Keep(row.Entry);
                Views.Main.Toast("Kept as " + Path.GetFileName(kept));
                _ = AppState.RefreshReplaysAsync();
            }
            catch (Exception ex)
            {
                Views.Main.Toast("Could not keep it: " + ex.Message, isError: true);
            }
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } row)
                return;
            RenameBox.Text = Path.GetFileNameWithoutExtension(row.Entry.FileName);
            DetailTitle.Visibility = Visibility.Collapsed;
            RenameBox.Visibility = Visibility.Visible;
            RenameBox.Focus();
            RenameBox.SelectAll();
        }

        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
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
        }

        private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (RenameBox.Visibility == Visibility.Visible)
                EndRename();
        }

        private void EndRename()
        {
            RenameBox.Visibility = Visibility.Collapsed;
            DetailTitle.Visibility = Visibility.Visible;
            List.Focus();
        }

        private void CommitRename()
        {
            if (Selected is not { } row)
                return;
            string old = row.Entry.FilePath;
            try
            {
                string renamed = ReplayService.Rename(row.Entry, RenameBox.Text);
                if (_starred.Remove(old))
                {
                    _starred.Add(renamed);
                    SaveStars();
                }
                EndRename();
                Views.Main.Toast("Renamed to " + Path.GetFileName(renamed), () =>
                {
                    try { File.Move(renamed, old); } catch { }
                    _ = AppState.RefreshReplaysAsync();
                });
                _ = AppState.RefreshReplaysAsync();
            }
            catch (Exception ex)
            {
                Views.Main.Toast(ex.Message, isError: true);
            }
        }

        private void Show_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is { } row)
                Views.ShowInFolder(row.Entry.FilePath);
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } row)
                return;
            try
            {
                Clipboard.SetFileDropList(new StringCollection { row.Entry.FilePath });
                Views.Main.Toast("Replay copied. Paste it into Discord or a folder.");
            }
            catch (Exception ex)
            {
                Views.Main.Toast("Could not copy: " + ex.Message, isError: true);
            }
        }

        // Deleted replays wait in a folder of their own so Undo can bring them back
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } row)
                return;
            string source = row.Entry.FilePath;
            try
            {
                string folder = Path.Combine(TrashFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(folder);
                string target = Path.Combine(folder, Path.GetFileName(source));
                File.Move(source, target);
                int index = List.SelectedIndex;
                _rows.Remove(row);
                List.SelectedIndex = Math.Min(index, List.Items.Count - 1);
                Views.Main.Toast($"Deleted {row.Map}", () =>
                {
                    try { File.Move(target, source); } catch (Exception ex) { Views.Main.Toast(ex.Message, isError: true); }
                    _ = AppState.RefreshReplaysAsync();
                });
                _ = AppState.RefreshReplaysAsync();
            }
            catch (Exception ex)
            {
                Views.Main.Toast("Could not delete: " + ex.Message, isError: true);
            }
        }

        private void Archive_Click(object sender, RoutedEventArgs e)
        {
            bool on = Archive.IsChecked == true;
            try
            {
                ReplayService.SetArchive(on);
                Views.Main.Toast(on ? "Every match is now archived by the game." : "Match archive turned off.", () =>
                {
                    ReplayService.SetArchive(!on);
                    Archive.IsChecked = !on;
                });
            }
            catch (Exception ex)
            {
                Archive.IsChecked = !on;
                Views.Main.Toast("Could not change Options.ini: " + ex.Message, isError: true);
            }
        }

        // ── Stars ──

        private static HashSet<string> LoadStars()
        {
            try
            {
                if (File.Exists(StarFile))
                    return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StarFile)) is { } set
                        ? new HashSet<string>(set, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new(StringComparer.OrdinalIgnoreCase);
        }

        private void SaveStar(ReplayRow row)
        {
            if (row.Starred) _starred.Add(row.Entry.FilePath);
            else _starred.Remove(row.Entry.FilePath);
            SaveStars();
            TabStarred.SetValue(Ui.BadgeProperty, _rows.Count(r => r.Starred).ToString());
        }

        private void SaveStars()
        {
            try
            {
                Directory.CreateDirectory(GamePaths.AppData);
                File.WriteAllText(StarFile, JsonSerializer.Serialize(_starred));
            }
            catch { }
        }
    }
}
