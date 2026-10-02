using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public partial class AddonsPage : UserControl, IPage
    {
        private static readonly BitmapImage OriginalArt = Art("original");
        private static readonly BitmapImage ProArt = Art("pro");
        private static readonly BitmapImage ObserverArt = Art("observer");

        private (BarKind Kind, string? Resolution) _installed;
        private bool _loading, _dragging, _busy;
        private double _split = 0.5;
        private readonly DockPanel _footer = new();
        private readonly TextBlock _change = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _changeDetail = new() { FontSize = 13, Foreground = Views.Res("Text2"), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _apply = new() { Content = "Apply" };
        private readonly Border _topRight = new();

        public AddonsPage()
        {
            InitializeComponent();
            ROriginal.Tag = OriginalArt;
            RPro.Tag = ProArt;
            RObserver.Tag = ObserverArt;
            Before.Source = OriginalArt;

            _loading = true;
            foreach (var p in AddonService.Packages.Where(p => p.Kind == BarKind.Pro))
                Resolution.Items.Add(new ComboBoxItem { Content = p.Resolution.Replace("x", " × "), Tag = p.Resolution });
            _loading = false;

            foreach (var (mark, text) in new[]
            {
                (Mark.Ok, "Checked with SHA-256 before anything is installed"),
                (Mark.Ok, "Only the .big archives are copied; scripts in the package are skipped"),
                (Mark.Ok, "Changes the interface only, so it never causes a mismatch"),
                (Mark.Ok, "Your previous bar is kept; Undo puts it back"),
                (Mark.Info, "Source: TheSuperHackers/GeneralsControlBar on GitHub (MIT licence)"),
            })
            {
                var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                var m = new StatusMark { Mark = mark, Width = 14, Height = 14, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(m, Dock.Left);
                row.Children.Add(m);
                row.Children.Add(new TextBlock { Text = text, Style = Views.Style("Body"), FontSize = 13.5 });
                Safety.Children.Add(row);
            }

            BuildFooter();
            ReadInstalled();
        }

        private static BitmapImage Art(string name)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Assets/ControlBars/{name}.jpg");
            image.DecodePixelWidth = 1280;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        public string Title => "Add-ons";
        public string Crumb => "Control bars";
        public string IconKey => "I.cube";
        public FrameworkElement? TopRight => _topRight;
        public FrameworkElement? Footer => _footer;
        public bool HasPendingChanges => Pending;

        public void OnShown() => ReadInstalled();

        private void ReadInstalled()
        {
            _installed = GamePaths.GameFound ? AddonService.Installed() : (BarKind.Original, null);
            _loading = true;
            (_installed.Kind switch { BarKind.Pro => RPro, BarKind.Observer => RObserver, _ => ROriginal }).IsChecked = true;
            SelectResolution(_installed.Resolution ?? AddonService.SuggestedResolution());
            _loading = false;

            string now = _installed.Kind switch
            {
                BarKind.Pro => "Control Bar Pro " + (_installed.Resolution?.Replace("x", " × ") ?? ""),
                BarKind.Observer => "Observer bar",
                _ => "Original bar",
            };
            _topRight.Child = null;
            var chip = Views.Chip("cube", "Installed now: " + now.Trim(), Mark.Ok);
            _topRight.Child = chip;
            Update();
        }

        private void SelectResolution(string resolution)
        {
            foreach (ComboBoxItem item in Resolution.Items)
                if ((string)item.Tag == resolution)
                    Resolution.SelectedItem = item;
            if (Resolution.SelectedItem == null)
                Resolution.SelectedIndex = 2;
        }

        private BarKind Chosen => RPro.IsChecked == true ? BarKind.Pro : RObserver.IsChecked == true ? BarKind.Observer : BarKind.Original;
        private string? ChosenResolution => Chosen == BarKind.Pro ? (Resolution.SelectedItem as ComboBoxItem)?.Tag as string : null;
        private bool Pending => Chosen != _installed.Kind || (Chosen == BarKind.Pro && ChosenResolution != _installed.Resolution);

        private void Choice_Checked(object sender, RoutedEventArgs e)
        {
            if (!_loading) Update();
        }

        private void Resolution_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            RPro.IsChecked = true;
            Update();
        }

        private void Update()
        {
            var kind = Chosen;
            After.Source = kind switch { BarKind.Pro => ProArt, BarKind.Observer => ObserverArt, _ => OriginalArt };
            AfterLabel.Text = kind switch { BarKind.Pro => "Control Bar Pro", BarKind.Observer => "Observer bar", _ => "Original" };
            CompareNote.Text = kind switch
            {
                BarKind.Pro => "Pictures from TheSuperHackers' release page. Pick the size that matches your game resolution; a wrong size looks stretched.",
                BarKind.Observer => "Shows each player's money, power and units at the bottom; best for watching replays and casting.",
                _ => "Choose another bar to compare it with the original.",
            };
            ApplySplit();

            bool pending = Pending;
            _footer.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            if (pending)
            {
                string from = _installed.Kind switch { BarKind.Pro => "Control Bar Pro " + _installed.Resolution, BarKind.Observer => "Observer bar", _ => "Original" };
                string to = kind switch { BarKind.Pro => "Control Bar Pro " + ChosenResolution, BarKind.Observer => "Observer bar", _ => "Original" };
                _change.Text = $"{from} → {to}";
                var package = AddonService.Package(kind, ChosenResolution);
                _changeDetail.Text = package == null
                    ? "Moves the add-on archives out of the game folder; Undo puts them back."
                    : $"Downloads {Views.Size(package.Size)} from GitHub, checks it, then adds the .big files to the game folder.";
            }
            if (IsLoaded)
                Views.Main.RefreshFooter();
        }

        // ── Compare slider ──

        private void Compare_Down(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            CompareBox.CaptureMouse();
            Compare_Move(sender, e);
        }

        private void Compare_Move(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _split = Math.Clamp(e.GetPosition(CompareBox).X / Math.Max(1, CompareBox.ActualWidth), 0, 1);
            ApplySplit();
        }

        private void Compare_Up(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            CompareBox.ReleaseMouseCapture();
        }

        private void Compare_SizeChanged(object sender, SizeChangedEventArgs e) => ApplySplit();

        private void ApplySplit()
        {
            double w = CompareBox.ActualWidth, h = CompareBox.ActualHeight;
            if (w <= 0) return;
            double x = w * _split;
            After.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, w - x), h));
            Divider.Margin = new Thickness(x - 1, 0, 0, 0);
            Handle.Margin = new Thickness(x - 17, 0, 0, 0);
        }

        // ── Apply ──

        private void BuildFooter()
        {
            _apply.Style = Views.Style("BtnLg");
            Ui.SetVariant(_apply, Variant.Primary);
            Ui.SetIcon(_apply, Views.Icon("check"));
            _apply.Click += async (_, _) => await ApplyAsync();
            DockPanel.SetDock(_apply, Dock.Right);
            var discard = new Button { Content = "Discard", Style = Views.Style("BtnLg"), Margin = new Thickness(0, 0, 12, 0) };
            Ui.SetVariant(discard, Variant.Ghost);
            discard.Click += (_, _) => ReadInstalled();
            DockPanel.SetDock(discard, Dock.Right);
            _footer.Children.Add(_apply);
            _footer.Children.Add(discard);
            var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new StatusMark { Mark = Mark.Changed, Width = 14, Height = 14, Margin = new Thickness(0, 0, 10, 0) });
            text.Children.Add(_change);
            text.Children.Add(new TextBlock { Text = "   ·   ", Foreground = Views.Res("Text3"), VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(_changeDetail);
            _footer.Children.Add(text);
            _footer.Visibility = Visibility.Collapsed;
        }

        private async Task ApplyAsync()
        {
            if (_busy) return;
            _busy = true;
            _apply.IsEnabled = false;
            var progress = new Progress<string>(s => _changeDetail.Text = s);
            try
            {
                var made = await AddonService.ApplyAsync(Chosen, ChosenResolution, progress);
                Views.Main.Toast("Control bar changed. It shows the next time the game starts.", () =>
                {
                    AddonService.Undo(made);
                    ReadInstalled();
                    Views.Main.Toast("Previous control bar put back");
                });
            }
            catch (Exception ex)
            {
                Views.Main.Toast(ex.Message, isError: true);
            }
            finally
            {
                _busy = false;
                _apply.IsEnabled = true;
                ReadInstalled();
                _ = AppState.RefreshHealthAsync();
            }
        }

        private void Hd_Click(object sender, RoutedEventArgs e) => Views.Open(AddonService.HdPage);
    }
}
