using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages.Tools
{
    // One map in the Library or the Installed tab
    public sealed class MapTile : INotifyPropertyChanged
    {
        private BitmapSource? _preview;
        private bool _checked, _noPreview;

        public MapTile(MapInfo info, bool library, MapLibraryClient? client)
        {
            Info = info;
            IsLibrary = library;
            Client = client;
        }

        public MapInfo Info { get; }
        public bool IsLibrary { get; }
        public MapLibraryClient? Client { get; }   // where a catalog map's preview and zip come from

        // Identifies the preview picture, so a tile made again for the same map finds it in memory
        public string PreviewKey => Client != null && Info.Entry != null ? Client.Source + "|" + Info.Entry.Id : Info.PreviewPath ?? Info.Folder;

        public string Name => Loc.Ltr(Info.Name);

        public string Detail
        {
            get
            {
                string size = Loc.Ltr(Views.Size(Info.SizeBytes));
                return Info.Players > 0 ? Loc.N(Info.Players, "{0} player", "{0} players") + " · " + size : size;
            }
        }

        // On the picture: the player count and the download size
        public string PlayersText => Info.Players > 0 ? Info.Players.ToString(Loc.Culture) : "";
        public Visibility PlayersVisibility => Info.Players > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string SizeText => Views.Size(Info.SizeBytes);

        public string Tip
        {
            get
            {
                var lines = new List<string> { Info.Name };
                if (Info.CustomRules)
                    lines.Add(Loc.T("Its map.ini changes unit rules on this map."));
                if (Info.Scripts)
                    lines.Add(Loc.T("Uses map scripts."));
                return string.Join("\n", lines);
            }
        }

        public bool Selectable => !IsLibrary || !Info.Installed;
        public Cursor? Cursor => Selectable ? Cursors.Hand : null;
        public Visibility CheckVisibility => Selectable ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InstalledVisibility => IsLibrary && Info.Installed ? Visibility.Visible : Visibility.Collapsed;
        public double ImageOpacity => IsLibrary && Info.Installed ? 0.45 : 1;
        public Visibility NoPreviewVisibility => _noPreview ? Visibility.Visible : Visibility.Collapsed;

        // On screen right now; the preview loader skips tiles that scrolled away before their turn
        public bool OnScreen { get; set; }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value)
                    return;
                _checked = value;
                Raise(nameof(Checked));
            }
        }

        public BitmapSource? Preview
        {
            get => _preview;
            set
            {
                if (_preview == value)
                    return;
                _preview = value;
                Raise(nameof(Preview));
            }
        }

        public bool NoPreview
        {
            get => _noPreview;
            set
            {
                _noPreview = value;
                Raise(nameof(NoPreviewVisibility));
            }
        }

        // After the player count was read or the map was installed
        public void Refresh() => Raise("");

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // Loads the previews of the tiles on screen, the most recent request first and a few at a time. The latest
    // pictures stay in memory so scrolling back shows them at once; tiles off screen let go of theirs.
    internal sealed class PreviewLoader
    {
        private const int Parallel = 4;
        private const int Keep = 360;

        private readonly List<MapTile> _wanted = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> _cache = new();
        private readonly LinkedList<(string Key, BitmapSource Image)> _recent = new();
        private readonly HashSet<string> _missing = new();
        private int _running;

        public bool Idle => _wanted.Count == 0 && _running == 0;

        public void Want(MapTile tile)
        {
            tile.OnScreen = true;
            if (tile.Preview != null || tile.NoPreview)
                return;
            if (_cache.TryGetValue(tile.PreviewKey, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                tile.Preview = node.Value.Image;
                return;
            }
            if (_missing.Contains(tile.PreviewKey))
            {
                tile.NoPreview = true;
                return;
            }
            _wanted.Remove(tile);
            _wanted.Add(tile);
            Pump();
        }

        public void Drop(MapTile tile)
        {
            tile.OnScreen = false;
            _wanted.Remove(tile);
            tile.Preview = null;
        }

        private void Pump()
        {
            while (_running < Parallel && _wanted.Count > 0)
            {
                var tile = _wanted[^1];
                _wanted.RemoveAt(_wanted.Count - 1);
                _running++;
                _ = LoadAsync(tile);
            }
        }

        private async Task LoadAsync(MapTile tile)
        {
            BitmapSource? image = null;
            try
            {
                image = await Task.Run(() => ReadAsync(tile));
            }
            catch { }
            _running--;
            if (image != null)
                Remember(tile.PreviewKey, image);
            else
                _missing.Add(tile.PreviewKey);
            if (tile.OnScreen)
            {
                if (image != null)
                    tile.Preview = image;
                else
                    tile.NoPreview = true;
            }
            Pump();
        }

        private void Remember(string key, BitmapSource image)
        {
            if (_cache.ContainsKey(key))
                return;
            _cache[key] = _recent.AddFirst((key, image));
            while (_recent.Count > Keep)
            {
                _cache.Remove(_recent.Last!.Value.Key);
                _recent.RemoveLast();
            }
        }

        // Catalog maps: a PNG from the library folder or the download cache. Installed maps: the .tga next to the map.
        private static async Task<BitmapSource?> ReadAsync(MapTile tile)
        {
            if (tile.Client != null && tile.Info.Entry != null)
                return await tile.Client.PreviewFileAsync(tile.Info.Entry).ConfigureAwait(false) is { } file ? Png(file) : null;
            return tile.Info.PreviewPath != null ? TgaImage.Load(tile.Info.PreviewPath) : null;
        }

        private static BitmapSource? Png(string file)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }

    // Map library: maps from the online catalog (or a folder) to tick and download, and the maps already installed
    // in the user's Maps folder, with Recycle Bin removal and installing by dropping a .zip or a folder.
    public partial class MapsSection : UserControl, IToolSection
    {
        private const double MinTile = 150;
        private const double TileGap = 12;
        private const double ScrollBarRoom = 12;

        public static readonly DependencyProperty TileWidthProperty =
            DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(MapsSection), new PropertyMetadata(MinTile));

        public double TileWidth
        {
            get => (double)GetValue(TileWidthProperty);
            set => SetValue(TileWidthProperty, value);
        }

        private MapLibraryClient? _client;
        private CatalogResult? _catalog;
        private string? _folder;   // a plain folder of map folders used as the library
        private List<MapTile> _library = new(), _installed = new(), _filtered = new();
        private PreviewLoader _loader = new();
        private CancellationTokenSource? _fill, _install;
        private int _columns = 5;
        private bool _started, _loaded, _installing;
        private MapTile? _anchor;
        private int _players;   // the player filter: all, 2, 3-4, 5-6, 7-8

        public MapsSection()
        {
            InitializeComponent();
            RecycleUndo.CleanUp();
            UpdateAll(scrollToTop: true);
        }

        private bool LibraryTab => TabLibrary.IsChecked == true;
        private bool OnlineSource => _client?.IsOnlineLibrary == true;

        public bool HasPendingChanges => false;

        public void OnShown()
        {
            if (!_started)
            {
                _started = true;
                _ = LoadAsync();
            }
            else if (_loaded && !_installing)
            {
                // Maps may have been added or removed outside Command Center
                _ = RefreshInstalledAsync();
            }
        }

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 160 && !_loaded; i++)
                await Task.Delay(250);
            await Task.Delay(500);
            for (int i = 0; i < 120 && !_loader.Idle; i++)
                await Task.Delay(250);
            await Task.Delay(300);
        }

        public void ShowPart(string part)
        {
            if (part == "installed")
                TabInstalled.IsChecked = true;
            else if (part == "library")
                TabLibrary.IsChecked = true;
        }

        // ── Loading ──

        private async Task LoadAsync(bool refresh = false)
        {
            _fill?.Cancel();
            _loaded = false;
            UpdateAll(scrollToTop: true);

            // A web library or a folder with catalog.json is read through the client; any other folder as map folders
            var client = MapLibraryClient.ForCurrentSource();
            var catalog = client != null ? await client.LoadCatalogAsync(refresh) : null;
            string? folder = client == null ? MapLibraryClient.CurrentSource : null;
            var (library, installed) = await Task.Run(() =>
            {
                var inst = MapCatalog.Scan(GamePaths.Maps);
                List<MapInfo> lib;
                if (catalog?.Catalog != null)
                {
                    lib = MapCatalog.FromCatalog(catalog.Catalog);
                }
                else if (folder != null)
                {
                    lib = MapCatalog.Scan(folder);
                    var names = inst.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in lib)
                        m.Installed = names.Contains(m.Name);
                }
                else
                {
                    lib = new List<MapInfo>();
                }
                return (lib, inst);
            });

            var ticked = Ticked(_library);
            _client = client;
            _catalog = catalog;
            _folder = folder;
            _loader = new PreviewLoader();
            _library = Sorted(library).Select(m => new MapTile(m, library: true, client) { Checked = !m.Installed && ticked.Contains(m.Name) }).ToList();
            _installed = InstalledTiles(installed);
            _loaded = true;
            UpdateAll(scrollToTop: true);
            FillPlayerCounts();
        }

        // Rereads the Maps folder after an install or removal; the library tiles follow
        private async Task RefreshInstalledAsync()
        {
            var installed = await Task.Run(() => MapCatalog.Scan(GamePaths.Maps));
            var names = installed.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var tile in _library)
            {
                bool now = names.Contains(tile.Info.Name);
                if (tile.Info.Installed == now)
                    continue;
                tile.Info.Installed = now;
                if (now)
                    tile.Checked = false;
                tile.Refresh();
            }
            _installed = InstalledTiles(installed);
            UpdateAll(scrollToTop: false);
            FillPlayerCounts();
        }

        private List<MapTile> InstalledTiles(List<MapInfo> maps)
        {
            var ticked = Ticked(_installed);
            return Sorted(maps).Select(m =>
            {
                m.Installed = true;
                return new MapTile(m, library: false, null) { Checked = ticked.Contains(m.Name) };
            }).ToList();
        }

        private static HashSet<string> Ticked(IEnumerable<MapTile> tiles) =>
            tiles.Where(t => t.Checked).Select(t => t.Info.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<MapInfo> Sorted(IEnumerable<MapInfo> maps) =>
            maps.OrderBy(m => m.Name.TrimStart('!', ' ', '[', '('), StringComparer.OrdinalIgnoreCase);

        // Player counts of maps the game never cached are read from the map files in the background
        private void FillPlayerCounts()
        {
            _fill?.Cancel();
            var tiles = _library.Concat(_installed).Where(t => t.Info.Players == 0 && t.Info.Entry == null).ToList();
            if (tiles.Count == 0)
                return;
            _fill = new CancellationTokenSource();
            var token = _fill.Token;
            var byMap = tiles.GroupBy(t => t.Info).ToDictionary(g => g.Key, g => g.First());
            _ = Task.Run(() => MapCatalog.FillMissing(byMap.Keys, info =>
            {
                if (byMap.TryGetValue(info, out var tile))
                    Dispatcher.BeginInvoke(tile.Refresh);
            }, token));
        }

        // ── Filtering and rows of tiles ──

        private void UpdateAll(bool scrollToTop)
        {
            if (!IsInitialized)
                return;
            var source = LibraryTab ? _library : _installed;
            string q = Search.Text.Trim();
            (int min, int max) = _players switch
            {
                1 => (2, 2),
                2 => (3, 4),
                3 => (5, 6),
                4 => (7, 8),
                _ => (0, 99),
            };
            _filtered = source.Where(t => (q.Length == 0 || t.Info.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                                          && (min == 0 || (t.Info.Players >= min && t.Info.Players <= max))).ToList();
            BuildRows(scrollToTop);
            UpdateTabs();
            UpdateSummary();
            UpdateEmpty();
            UpdateBar();
        }

        private void BuildRows(bool scrollToTop)
        {
            var scroller = Scroller();
            double offset = scroller?.VerticalOffset ?? 0;
            var rows = new List<MapTile[]>();
            for (int i = 0; i < _filtered.Count; i += _columns)
                rows.Add(_filtered.GetRange(i, Math.Min(_columns, _filtered.Count - i)).ToArray());
            Rows.ItemsSource = rows;
            if (scroller == null)
                return;
            if (scrollToTop)
                scroller.ScrollToTop();
            else
                Dispatcher.BeginInvoke(() => scroller.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        }

        private ScrollViewer? Scroller()
        {
            Rows.ApplyTemplate();
            return Rows.Template.FindName("Scroller", Rows) as ScrollViewer;
        }

        // Tiles share each row evenly: as many as fit at MinTile or wider, each with a gap after it,
        // and room is kept for the scroll bar so the row does not jump when it appears
        private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double available = Rows.ActualWidth - ScrollBarRoom;
            if (available <= 0)
                return;
            int columns = Math.Max(1, (int)(available / (MinTile + TileGap)));
            TileWidth = Math.Max(40, Math.Floor(available / columns) - TileGap);
            if (columns != _columns)
            {
                _columns = columns;
                BuildRows(scrollToTop: false);
            }
        }

        private void UpdateTabs()
        {
            bool libraryRead = _loaded && (_catalog?.Catalog != null || _folder != null);
            TabLibrary.Content = libraryRead ? $"{Loc.T("LIBRARY")} ({_library.Count:N0})" : Loc.T("LIBRARY");
            TabInstalled.Content = _loaded ? $"{Loc.T("INSTALLED")} ({_installed.Count:N0})" : Loc.T("INSTALLED");
        }

        private void UpdateSummary()
        {
            Summary.Foreground = Views.Hint;
            Summary.ToolTip = null;
            if (!LibraryTab)
            {
                Summary.Text = _loaded
                    ? Loc.N(_installed.Count, "{0} map", "{0} maps") + " · " + Loc.Ltr(Views.Size(_installed.Sum(t => t.Info.SizeBytes)))
                    : "";
                Summary.ToolTip = GamePaths.Maps;
                return;
            }

            if (!_loaded)
            {
                Summary.Text = "";
                return;
            }
            if (_client != null)
            {
                string name = OnlineSource ? Loc.T("Online library") : Loc.Ltr(_client.Describe);
                Summary.ToolTip = _client.Source;
                if (_catalog is { Catalog: not null, Problem: { } problem })
                {
                    // The copy saved earlier is shown because the library could not be checked
                    Summary.Text = name + " · " + Loc.T(problem);
                    Summary.Foreground = Views.Warning;
                    Summary.ToolTip = _catalog.Detail != null ? Loc.T(_catalog.Detail) : null;
                }
                else if (_catalog?.Checked is { } when && _client.IsWeb)
                {
                    Summary.Text = name + " · " + Loc.T("checked {0}", Views.Ago(when));
                }
                else
                {
                    Summary.Text = name;
                }
            }
            else if (_folder != null)
            {
                Summary.Text = Loc.Ltr(_folder);
                Summary.ToolTip = _folder;
            }
        }

        private void UpdateEmpty()
        {
            bool library = LibraryTab;
            var source = library ? _library : _installed;
            bool empty = !_loaded || _filtered.Count == 0;
            Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyActions.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            if (!empty)
                return;

            if (!_loaded)
            {
                EmptyTitle.Text = library ? Loc.T("Reading the map library…") : Loc.T("Loading…");
                EmptyText.Text = "";
            }
            else if (library && _catalog is { Catalog: null } failed)
            {
                // Offline, not found, or a damaged catalog
                EmptyTitle.Text = Loc.T(failed.Problem ?? "The map library can't be read");
                EmptyText.Text = (failed.Detail != null ? Loc.T(failed.Detail) + " " : "") + Loc.T("You can still drop a .zip or a map folder on this page to install it.");
                EmptyActions.Visibility = Visibility.Visible;
                RetryButton.Visibility = Visibility.Visible;
            }
            else if (library && source.Count == 0)
            {
                EmptyTitle.Text = Loc.T("No maps in this folder");
                EmptyText.Text = Loc.T("Pick a folder that holds map folders (each with a .map file).");
            }
            else if (source.Count == 0)
            {
                EmptyTitle.Text = Loc.T("No maps installed");
                EmptyText.Text = Loc.T("Install maps from the Library tab, or drop a .zip or a map folder anywhere on this page.");
            }
            else
            {
                EmptyTitle.Text = Loc.T("Nothing matches");
                EmptyText.Text = Loc.T("Try another search or player count.");
            }
        }

        private void UpdateBar()
        {
            bool library = LibraryTab;
            LibraryActions.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
            InstalledActions.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
            if (_installing)
            {
                DownloadAll.IsEnabled = DownloadSelected.IsEnabled = false;
                return;
            }

            var picked = (library ? _library : _installed).Where(t => t.Checked && t.Selectable).ToList();
            Status.Foreground = picked.Count > 0 ? System.Windows.Media.Brushes.White : Views.Hint;
            string selection = Loc.N(picked.Count, "{0} map selected", "{0} maps selected") + " · " + Loc.Ltr(Views.Size(picked.Sum(t => t.Info.SizeBytes)));
            if (library)
            {
                bool available = _loaded && _library.Count > 0;
                int missing = _filtered.Count(t => !t.Info.Installed);
                DownloadAll.Visibility = DownloadSelected.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                DownloadAll.Content = missing > 0 ? $"{Loc.T("DOWNLOAD ALL")} ({missing:N0})" : Loc.T("DOWNLOAD ALL");
                DownloadAll.IsEnabled = missing > 0;
                DownloadSelected.IsEnabled = picked.Count > 0;
                DownloadSelected.Content = picked.Count > 0 ? $"{Loc.T("DOWNLOAD SELECTED")} ({picked.Count:N0})" : Loc.T("DOWNLOAD SELECTED");
                Status.Text = !available ? ""
                    : picked.Count > 0 ? selection
                    : missing == 0 && _filtered.Count > 0 ? Loc.T("Every map shown here is installed.")
                    : Loc.T("Tick the maps you want, or download all of them.");
            }
            else
            {
                DeleteButton.Visibility = picked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                DeleteButton.Content = picked.Count > 1 ? $"{Loc.T("DELETE")} ({picked.Count:N0})" : Loc.T("DELETE");
                DeleteButton.ToolTip = Loc.T("Moves the map to the Recycle Bin; Undo brings it back");
                FolderButton.Content = picked.Count == 1 ? Loc.T("SHOW IN FOLDER") : Loc.T("OPEN FOLDER");
                Status.Text = picked.Count > 0 ? selection : Loc.T("Drop a .zip or a map folder here to install it.");
            }
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            _anchor = null;
            UpdateAll(scrollToTop: true);
        }

        private void Players_Checked(object sender, RoutedEventArgs e)
        {
            _players = int.Parse((string)((FrameworkElement)sender).Tag);
            UpdateAll(scrollToTop: true);
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateAll(scrollToTop: true);
        }

        // ── Tiles ──

        private void Tile_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: MapTile tile })
                _loader.Want(tile);
        }

        private void Tile_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: MapTile tile })
                _loader.Drop(tile);
        }

        // A click ticks or unticks a map; Shift+click gives every map up to the last one clicked the same state
        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            if (_installing || sender is not FrameworkElement { DataContext: MapTile tile } || !tile.Selectable)
                return;
            int from = _anchor != null ? _filtered.IndexOf(_anchor) : -1;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && from >= 0)
            {
                int to = _filtered.IndexOf(tile);
                bool value = _anchor!.Checked;
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                {
                    if (_filtered[i].Selectable)
                        _filtered[i].Checked = value;
                }
            }
            else
            {
                tile.Checked = !tile.Checked;
            }
            _anchor = tile;
            UpdateBar();
        }

        // Reads the online library again after it could not be reached
        private void Retry_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(refresh: true);

        // ── Download and install ──

        private void DownloadSelected_Click(object sender, RoutedEventArgs e) =>
            _ = InstallAsync(_library.Where(t => t.Checked && !t.Info.Installed).ToList());

        private void DownloadAll_Click(object sender, RoutedEventArgs e)
        {
            var maps = _filtered.Where(t => !t.Info.Installed).ToList();
            if (maps.Count > 10)
            {
                string question = Loc.N(maps.Count, "Download and install {0} map ({1})?", "Download and install {0} maps ({1})?",
                    Loc.Ltr(Views.Size(maps.Sum(t => t.Info.SizeBytes))));
                if (MessageBox.Show(Window.GetWindow(this), question, Loc.T("Map library"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No, Loc.MessageBoxOptions) != MessageBoxResult.Yes)
                    return;
            }
            _ = InstallAsync(maps);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _install?.Cancel();

        // Catalog maps are downloaded, checked against the catalog and installed one by one; maps of a plain folder
        // are copied. The bar under the status text shows how far it is.
        private async Task InstallAsync(List<MapTile> tiles)
        {
            if (_installing || tiles.Count == 0)
                return;
            _installing = true;
            _install = new CancellationTokenSource();
            var token = _install.Token;
            Progress.Value = 0;
            Progress.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Visible;
            UpdateBar();

            var installed = new List<string>();
            var failed = new List<string>();
            int skipped = 0;
            bool cancelled = false;
            try
            {
                if (_client != null)
                {
                    var report = await _client.InstallAsync(tiles.Select(t => t.Info.Entry!).ToList(), new Progress<InstallProgress>(ShowProgress), token);
                    installed = report.Installed;
                    failed = report.Failed;
                    skipped = report.Skipped;
                    cancelled = report.Cancelled;
                }
                else
                {
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                        var map = tiles[i].Info;
                        ShowProgress(new InstallProgress(i + 1, tiles.Count, map.Name, i * 100 / tiles.Count, true));
                        try
                        {
                            if (await Task.Run(() => MapCatalog.Install(map)))
                                installed.Add(map.Name);
                            else
                                skipped++;
                        }
                        catch (Exception ex)
                        {
                            failed.Add($"{map.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failed.Add(ex.Message);
            }
            finally
            {
                _installing = false;
                Progress.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
            }

            foreach (var tile in tiles)
                tile.Checked = false;
            var parts = new List<string>();
            if (installed.Count == 1 && tiles.Count == 1)
                parts.Add(Loc.T("Installed {0}. It shows up in Skirmish and online games.", Loc.Ltr(installed[0])));
            else if (installed.Count > 0 || (failed.Count == 0 && skipped == 0))
                parts.Add(Loc.N(installed.Count, "Installed {0} map.", "Installed {0} maps."));
            if (skipped > 0)
                parts.Add(Loc.N(skipped, "{0} was already installed.", "{0} were already installed."));
            if (cancelled)
                parts.Add(Loc.T("The rest was not downloaded."));
            if (failed.Count > 0)
                parts.Add(Loc.T("Failed: {0}", string.Join("; ", failed.Take(3))));
            Views.Main.Toast(string.Join(" ", parts), isError: failed.Count > 0);
            await RefreshInstalledAsync();
        }

        private void ShowProgress(InstallProgress p)
        {
            if (!_installing)
                return;
            string name = Loc.Ltr(p.Name);
            Status.Foreground = System.Windows.Media.Brushes.White;
            Status.Text = p.Count > 1
                ? p.Copying
                    ? Loc.T("Map {0} of {1} · {2} · checked, copying files…", p.Number, p.Count, name)
                    : Loc.T("Map {0} of {1} · {2}% · {3}", p.Number, p.Count, p.Percent, name)
                : p.Copying
                    ? Loc.T("Installing {0}…", name)
                    : Loc.T("Downloading {0} · {1}%", name, p.Percent);
            Progress.Value = p.Percent;
        }

        // ── Installed maps ──

        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            var picked = _installed.Where(t => t.Checked).ToList();
            if (picked.Count == 1)
            {
                Views.ShowInFolder(picked[0].Info.Folder);
                return;
            }
            try { Directory.CreateDirectory(GamePaths.Maps); } catch { }
            Views.ShowInFolder(GamePaths.Maps);
        }

        private void AddMaps_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Choose map .zip files"),
                Filter = Loc.T("Map archives") + " (*.zip)|*.zip",
                Multiselect = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                _ = InstallFilesAsync(dialog.FileNames);
        }

        // The map folders go to the Recycle Bin, so Windows can also restore them later
        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var picked = _installed.Where(t => t.Checked).ToList();
            if (picked.Count == 0)
                return;
            var undo = RecycleUndo.Recycle(picked.Select(t => t.Info.Folder).ToList(), out var removed, out var failed);
            string message = removed.Count == 1
                ? Loc.T("Moved {0} to the Recycle Bin.", Loc.Ltr(Path.GetFileName(removed[0])))
                : Loc.N(removed.Count, "Moved {0} map to the Recycle Bin.", "Moved {0} maps to the Recycle Bin.");
            if (failed.Count > 0)
                message += " " + Loc.T("Failed: {0}", string.Join("; ", failed.Take(3)));
            Views.Main.Toast(message, removed.Count == 0 ? null : () =>
            {
                try
                {
                    undo();
                    Views.Main.Toast(Loc.T("Change undone"));
                }
                catch (Exception ex)
                {
                    Views.Main.Toast(Loc.T("Could not put it back: {0}", ex.Message), isError: true);
                }
                _ = RefreshInstalledAsync();
            }, isError: failed.Count > 0);
            await RefreshInstalledAsync();
        }

        // ── Drop a .zip or a map folder anywhere on the section ──

        private void Section_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                DropOverlay.Visibility = Visibility.Visible;
        }

        private void Overlay_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Overlay_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

        private void Overlay_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                _ = InstallFilesAsync(paths);
        }

        private async Task InstallFilesAsync(IReadOnlyList<string> paths)
        {
            TabInstalled.IsChecked = true;
            Status.Foreground = System.Windows.Media.Brushes.White;
            Status.Text = Loc.T("Installing…");
            var installed = new List<string>();
            var failed = new List<string>();
            await Task.Run(() =>
            {
                foreach (string path in paths)
                {
                    try { installed.AddRange(MapService.Install(path)); }
                    catch (Exception ex) { failed.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
                }
            });
            if (installed.Count == 0 && failed.Count == 0)
                Views.Main.Toast(Loc.T("No maps found. Drop a .zip or a folder that holds a .map file."), isError: true);
            else if (failed.Count > 0)
                Views.Main.Toast(Loc.N(installed.Count, "Installed {0} map.", "Installed {0} maps.") + " " + Loc.T("Failed: {0}", string.Join("; ", failed.Take(3))), isError: true);
            else if (installed.Count == 1)
                Views.Main.Toast(Loc.T("Installed {0}. It shows up in Skirmish and online games.", Loc.Ltr(installed[0])));
            else
                Views.Main.Toast(Loc.N(installed.Count, "Installed {0} map.", "Installed {0} maps."));
            await RefreshInstalledAsync();
        }
    }
}
