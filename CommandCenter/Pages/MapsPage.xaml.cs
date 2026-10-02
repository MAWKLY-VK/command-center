using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public sealed class MapCard : INotifyPropertyChanged
    {
        private BitmapSource? _preview;
        private bool _checked, _selected;

        public MapCard(MapInfo info, bool library, MapLibraryClient? client = null)
        {
            Info = info;
            IsLibrary = library;
            Client = client;
        }

        public MapInfo Info { get; }
        public MapLibraryClient? Client { get; }   // where a catalog map's preview and zip come from
        public bool IsLibrary { get; }
        public string Name => Info.Name;
        public string PlayersText => Info.Players > 0 ? Info.Players.ToString() : "?";
        public string SizeText => Views.Size(Info.SizeBytes);
        public Visibility CheckVisibility => IsLibrary && !Info.Installed ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InstalledVisibility => IsLibrary && Info.Installed ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoPreview => _preview == null ? Visibility.Visible : Visibility.Collapsed;
        public Brush Edge => _selected ? Views.Res("Accent") : _checked ? Views.Res("AccentLine") : Views.Res("Line");
        public bool PreviewRequested { get; set; }

        public BitmapSource? Preview
        {
            get => _preview;
            set { _preview = value; Raise(nameof(Preview)); Raise(nameof(NoPreview)); }
        }

        public bool Checked
        {
            get => _checked;
            set { _checked = value; Raise(nameof(Checked)); Raise(nameof(Edge)); CheckedChanged?.Invoke(); }
        }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; Raise(nameof(Edge)); }
        }

        public void Refresh()
        {
            Raise(nameof(PlayersText));
            Raise(nameof(CheckVisibility));
            Raise(nameof(InstalledVisibility));
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public event PropertyChangedEventHandler? PropertyChanged;
        public static event Action? CheckedChanged;
    }

    public partial class MapsPage : UserControl, IPage
    {
        private const int PageSize = 60;

        private List<MapCard> _library = new(), _installed = new();
        private List<MapCard> _filtered = new();
        private readonly ObservableCollection<MapCard> _shown = new();
        private MapCard? _selected;
        private readonly BlockingCollection<MapCard> _previewQueue = new();
        private CancellationTokenSource? _fill;
        private readonly DockPanel _footer = new();
        private readonly TextBlock _footerText = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Views.Res("Text2") };
        private readonly Button _installSelected = new() { Content = "Install selected" };
        private readonly Button _clear = new() { Content = "Clear" };
        private readonly Border _topRight;
        private bool _ready;

        public MapsPage()
        {
            InitializeComponent();
            Cards.ItemsSource = _shown;
            MapCard.CheckedChanged += UpdateFooter;

            _topRight = Views.Chip("folder", @"Documents › Zero Hour Data › Maps");
            _topRight.Cursor = Cursors.Hand;
            _topRight.ToolTip = GamePaths.Maps;
            _topRight.MouseLeftButtonUp += (_, _) => Views.ShowInFolder(GamePaths.Maps);

            BuildFooter();
            var worker = new Thread(PreviewWorker) { IsBackground = true, Name = "map previews" };
            worker.Start();
            _ = LoadAsync();
        }

        public string Title => "Maps";
        public string Crumb => TabInstalled.IsChecked == true ? "Installed maps" : "Map library";
        public string IconKey => "I.map";
        public FrameworkElement? TopRight => _topRight;
        public FrameworkElement? Footer => _footer;

        public void OnShown() => UpdateFooter();

        public void FocusSearch()
        {
            Search.Focus();
            Search.SelectAll();
        }

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 80 && !_ready; i++)
                await Task.Delay(250);
            await Task.Delay(1500);
        }

        private MapLibraryClient? _client;
        private CatalogResult? _catalog;

        private async Task LoadAsync()
        {
            _fill?.Cancel();
            // A web library or a folder with catalog.json is read through the client; any other folder as map folders
            var client = MapLibraryClient.ForCurrentSource();
            var catalog = client != null ? await client.LoadCatalogAsync() : null;
            _client = client;
            _catalog = catalog;
            string? library = client == null ? MapLibraryClient.CurrentSource : null;
            var (lib, inst) = await Task.Run(() =>
            {
                var installed = MapCatalog.Scan(GamePaths.Maps);
                var names = installed.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (catalog?.Catalog != null)
                    return (MapCatalog.FromCatalog(catalog.Catalog), installed);
                var libraryMaps = library != null ? MapCatalog.Scan(library) : new List<MapInfo>();
                foreach (var m in libraryMaps)
                    m.Installed = names.Contains(m.Name);
                return (libraryMaps, installed);
            });
            _library = lib.Select(m => new MapCard(m, library: true, client)).ToList();
            _installed = inst.Select(m => { m.Installed = true; return new MapCard(m, library: false); }).ToList();
            Ui.SetBadge(TabLibrary, _library.Count.ToString("N0"));
            Ui.SetBadge(TabInstalled, _installed.Count.ToString("N0"));
            ApplyFilter();
            _ready = true;

            // Player counts for maps the game never cached are read in the background
            _fill = new CancellationTokenSource();
            var token = _fill.Token;
            var all = _library.Concat(_installed).ToList();
            _ = Task.Run(() => MapCatalog.FillMissing(all.Select(c => c.Info), info =>
            {
                var card = all.FirstOrDefault(c => c.Info == info);
                if (card != null)
                    Dispatcher.BeginInvoke(card.Refresh);
            }, token));
        }

        // ── Filtering and paging ──

        private void ApplyFilter()
        {
            bool library = TabLibrary.IsChecked == true;
            var source = library ? _library : _installed;
            string q = Search.Text.Trim();
            (int min, int max) = P2.IsChecked == true ? (2, 2) : P4.IsChecked == true ? (3, 4) : P6.IsChecked == true ? (5, 6) : P8.IsChecked == true ? (7, 8) : (0, 99);
            var list = source.Where(c => (q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                                         && (min == 0 || (c.Info.Players >= min && c.Info.Players <= max)));
            list = Sort.SelectedIndex switch
            {
                1 => list.OrderByDescending(c => c.Info.Players).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
                2 => list.OrderByDescending(c => c.Info.Modified),
                3 => list.OrderByDescending(c => c.Info.SizeBytes),
                _ => list.OrderBy(c => c.Name.TrimStart('!', ' ', '[', '('), StringComparer.OrdinalIgnoreCase),
            };
            _filtered = list.ToList();
            _shown.Clear();
            ShowMore();
            Scroller.ScrollToTop();

            // The library could not be read (offline, 404, damaged catalog), or the chosen folder holds no maps
            bool noLibrary = library && source.Count == 0 && (_catalog is { Catalog: null } || _client == null);
            Empty.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChooseLibrary.Visibility = noLibrary ? Visibility.Visible : Visibility.Collapsed;
            ChooseLibrary.Content = _client == null ? "Choose another folder" : "Use a local folder";
            if (_catalog is { Catalog: null } failed && library)
            {
                EmptyTitle.Text = failed.Problem ?? "The map library can't be read";
                EmptyText.Text = failed.Detail + " You can also install maps from a folder on this PC.";
            }
            else
            {
                EmptyTitle.Text = noLibrary ? "No maps in this folder" : source.Count == 0 ? "No maps installed" : "Nothing matches";
                EmptyText.Text = noLibrary
                    ? "Pick a folder that holds map folders (each with a .map file)."
                    : source.Count == 0 ? "Install maps from the Library tab, or drop a .zip or a map folder anywhere on this page." : "Try another search or player count.";
            }

            if (_selected == null || !_filtered.Contains(_selected))
                Select(_filtered.FirstOrDefault());
            UpdateFooter();
        }

        private void ShowMore()
        {
            foreach (var card in _filtered.Skip(_shown.Count).Take(PageSize))
            {
                _shown.Add(card);
                if (!card.PreviewRequested)
                {
                    card.PreviewRequested = true;
                    _previewQueue.Add(card);
                }
            }
            Counter.Text = _filtered.Count == 0 ? "" : $"Showing {_shown.Count:N0} of {_filtered.Count:N0}";
        }

        public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(MapsPage), new PropertyMetadata(196.0));
        public static readonly DependencyProperty CardImageHeightProperty = DependencyProperty.Register(nameof(CardImageHeight), typeof(double), typeof(MapsPage), new PropertyMetadata(150.0));
        public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }
        public double CardImageHeight { get => (double)GetValue(CardImageHeightProperty); set => SetValue(CardImageHeightProperty, value); }

        // Cards share the row evenly: as many as fit at 180 px or more. Each card carries a 14 px right margin,
        // and the scroll bar takes about 12 px.
        private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double available = Scroller.ActualWidth - 14;
            int columns = Math.Max(1, (int)(available / (180 + 14)));
            CardWidth = Math.Floor(available / columns) - 14;
            CardImageHeight = Math.Round(CardWidth * 0.76);
        }

        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Scroller.ScrollableHeight - Scroller.VerticalOffset < 400 && _shown.Count < _filtered.Count)
                ShowMore();
        }

        private void PreviewWorker()
        {
            foreach (var card in _previewQueue.GetConsumingEnumerable())
            {
                BitmapSource? image = null;
                if (card.Client != null && card.Info.Entry != null)
                {
                    // Catalog maps: a PNG from the library folder or the download cache
                    string? file = card.Client.PreviewFileAsync(card.Info.Entry).GetAwaiter().GetResult();
                    image = file != null ? MainWindow.LoadBitmap(file) : null;
                }
                else if (card.Info.PreviewPath != null)
                {
                    image = TgaImage.Load(card.Info.PreviewPath);
                }
                if (image == null)
                    continue;
                Dispatcher.BeginInvoke(() =>
                {
                    card.Preview = image;
                    if (card == _selected)
                        BigImage.Source = image;
                });
            }
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || Cards == null) return;
            ApplyFilter();
            Views.Main.RefreshHeader();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || Cards == null) return;
            ApplyFilter();
        }

        private void Search_Changed(object sender, TextChangedEventArgs e) => Filter_Changed(sender, e);

        // ── Selection and details ──

        private void Card_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) != null)
                return;
            if (sender is FrameworkElement { DataContext: MapCard card })
                Select(card);
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            for (var d = child; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is T t) return t;
            return null;
        }

        private void Select(MapCard? card)
        {
            if (_selected != null)
                _selected.Selected = false;
            _selected = card;
            Details.Visibility = card == null ? Visibility.Collapsed : Visibility.Visible;
            NoSelection.Visibility = card == null ? Visibility.Visible : Visibility.Collapsed;
            if (card == null)
                return;
            card.Selected = true;
            var m = card.Info;
            BigImage.Source = card.Preview ?? (m.PreviewPath != null ? TgaImage.Load(m.PreviewPath) : null);
            DKicker.Text = card.IsLibrary ? (m.Installed ? "Library · installed" : "Library") : "Installed";
            DName.Text = m.Name;
            DPlayers.Text = m.Players > 0 ? $"{m.Players}" + (m.Players == 2 ? " · 1v1" : m.Players == 4 ? " · 2v2 or FFA" : m.Players == 6 ? " · 3v3 or FFA" : m.Players == 8 ? " · 4v4 or FFA" : "") : "Counting…";
            DSize.Text = Views.Size(m.SizeBytes);
            InstallBtn.Visibility = card.IsLibrary && !m.Installed ? Visibility.Visible : Visibility.Collapsed;
            RemoveBtn.Visibility = !card.IsLibrary ? Visibility.Visible : Visibility.Collapsed;
            ShowStarts(m);
        }

        // Numbered start positions over the preview, from the game's map cache
        private void ShowStarts(MapInfo map)
        {
            StartLayer.Children.Clear();
            BigPreview.UpdateLayout();
            double w = BigPreview.ActualWidth, h = BigPreview.ActualHeight;
            if (w <= 0) w = 310;
            if (h <= 0) h = 300;
            int n = 1;
            foreach (var p in map.Starts)
            {
                var dot = new Grid { Width = 26, Height = 26 };
                dot.Children.Add(new Ellipse { Fill = Views.Res("GoldFill"), Stroke = (Brush)new BrushConverter().ConvertFrom("#1D1405")!, StrokeThickness = 1.5 });
                dot.Children.Add(new TextBlock { Text = n++.ToString(), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = (Brush)new BrushConverter().ConvertFrom("#1D1405")!, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                Canvas.SetLeft(dot, Math.Clamp(p.X * w - 13, 0, w - 26));
                Canvas.SetTop(dot, Math.Clamp(p.Y * h - 13, 0, h - 26));
                StartLayer.Children.Add(dot);
            }
        }

        // ── Install, remove ──

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null)
                Install(new[] { _selected });
        }

        private async void Install(IReadOnlyList<MapCard> cards)
        {
            if (cards.Count > 0 && cards[0].Client is { } client)
            {
                await InstallFromCatalogAsync(client, cards);
                return;
            }
            int done = 0, skipped = 0;
            var failed = new List<string>();
            await Task.Run(() =>
            {
                foreach (var card in cards)
                {
                    try
                    {
                        if (MapCatalog.Install(card.Info)) done++;
                        else skipped++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{card.Name}: {ex.Message}");
                    }
                }
            });
            foreach (var card in cards)
                card.Checked = false;
            string message = done == 1 && cards.Count == 1 ? $"Installed {cards[0].Name}. It shows up in Skirmish and online games." : $"Installed {done} maps.";
            if (skipped > 0) message += $" {skipped} were already installed.";
            if (failed.Count > 0)
                Views.Main.Toast(message + " Failed: " + string.Join("; ", failed.Take(3)), isError: true);
            else
                Views.Main.Toast(message);
            await LoadAsync();
        }

        // Downloads, checks and installs catalog maps one by one; the footer shows the progress
        private bool _installing;

        private async Task InstallFromCatalogAsync(MapLibraryClient client, IReadOnlyList<MapCard> cards)
        {
            if (_installing)
                return;
            _installing = true;
            InstallBtn.IsEnabled = _installSelected.IsEnabled = false;
            InstallReport report;
            try
            {
                report = await client.InstallAsync(cards.Select(c => c.Info.Entry!).ToList(), new Progress<string>(text => _footerText.Text = text));
            }
            finally
            {
                _installing = false;
                InstallBtn.IsEnabled = _installSelected.IsEnabled = true;
            }
            foreach (var card in cards)
                card.Checked = false;
            string message = report.Installed.Count == 1 && cards.Count == 1
                ? $"Installed {report.Installed[0]}. It shows up in Skirmish and online games."
                : $"Installed {report.Installed.Count} maps.";
            if (report.Skipped > 0) message += $" {report.Skipped} were already installed.";
            if (report.Failed.Count > 0)
                Views.Main.Toast(message + " Failed: " + string.Join("; ", report.Failed.Take(3)), isError: true);
            else
                Views.Main.Toast(message);
            await LoadAsync();
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _selected.IsLibrary)
                return;
            var map = _selected.Info;
            try
            {
                string moved = MapCatalog.Remove(map);
                Views.Main.Toast($"Removed {map.Name}", () =>
                {
                    try { Directory.Move(moved, map.Folder); } catch (Exception ex) { Views.Main.Toast(ex.Message, isError: true); }
                    _ = LoadAsync();
                });
            }
            catch (Exception ex)
            {
                Views.Main.Toast("Could not remove it: " + ex.Message, isError: true);
            }
            _selected = null;
            await LoadAsync();
        }

        private void ShowFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null)
                Views.ShowInFolder(_selected.Info.MapFile);
        }

        private void ChooseLibrary_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder that holds your maps" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            MapLibraryClient.UseSource(dialog.FolderName);
            _ = LoadAsync();
        }

        // Drop a .zip or a map folder anywhere on the page to install it
        private void Page_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Page_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
                return;
            var installed = new List<string>();
            var failed = new List<string>();
            await Task.Run(() =>
            {
                foreach (string path in paths)
                {
                    try { installed.AddRange(MapService.Install(path)); }
                    catch (Exception ex) { failed.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}"); }
                }
            });
            if (installed.Count == 0 && failed.Count == 0)
                Views.Main.Toast("No maps found. Drop a .zip or a folder that holds a .map file.", isError: true);
            else if (failed.Count > 0)
                Views.Main.Toast($"Installed {installed.Count}. Failed: {string.Join("; ", failed)}", isError: true);
            else
                Views.Main.Toast(installed.Count == 1 ? $"Installed {installed[0]}" : $"Installed {installed.Count} maps");
            TabInstalled.IsChecked = true;
            await LoadAsync();
        }

        // ── Footer ──

        private void BuildFooter()
        {
            _installSelected.Style = Views.Style("BtnLg");
            Ui.SetVariant(_installSelected, Variant.Primary);
            Ui.SetIcon(_installSelected, Views.Icon("download"));
            _installSelected.Click += (_, _) => Install(_library.Where(c => c.Checked).ToList());
            DockPanel.SetDock(_installSelected, Dock.Right);
            _clear.Style = Views.Style("BtnLg");
            _clear.Margin = new Thickness(0, 0, 12, 0);
            Ui.SetVariant(_clear, Variant.Ghost);
            _clear.Click += (_, _) =>
            {
                foreach (var c in _library.Where(c => c.Checked).ToList())
                    c.Checked = false;
            };
            DockPanel.SetDock(_clear, Dock.Right);
            _footer.Children.Add(_installSelected);
            _footer.Children.Add(_clear);
            var hint = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            hint.Children.Add(new Controls.Icon { Data = Views.Icon("info"), Width = 17, Height = 17, Foreground = Views.Res("IconIdle"), Margin = new Thickness(0, 0, 10, 0) });
            hint.Children.Add(_footerText);
            _footer.Children.Add(hint);
        }

        private void UpdateFooter()
        {
            var selected = _library.Where(c => c.Checked).ToList();
            bool any = selected.Count > 0;
            _installSelected.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _clear.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _installSelected.Content = any ? $"Install {selected.Count} selected" : "Install selected";
            _footerText.Text = any
                ? $"{selected.Count} selected · {Views.Size(selected.Sum(c => c.Info.SizeBytes))} · Maps go to your Documents folder, not the game folder"
                : "Maps go to your Documents folder, never the game folder. Drop a .zip or map folder here to install it.";
            if (IsLoaded)
                Views.Main.RefreshFooter();
        }
    }
}
