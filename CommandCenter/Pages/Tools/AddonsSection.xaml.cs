using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages.Tools
{
    // Add-ons: the control bar (the game's own or Control Bar Pro) with a before/after picture,
    // and hotkey letters drawn on the button pictures.
    public partial class AddonsSection : UserControl, IToolSection
    {
        // The pictures are 1920 x 1080 screenshots; the preview shows the bottom of the screen, where the bar is
        private const double CropTop = 700.0 / 1080;
        private const double MaxPreviewHeight = 300;
        private const int PreviewIcons = 10;

        private static readonly BitmapSource OriginalArt = Art("original");
        private static readonly BitmapSource ProArt = Art("pro");
        private static readonly SolidColorBrush NoteBrush = Views.Brush("#A8A8C8");
        private static readonly SolidColorBrush NoteDot = Views.Brush("#404070");
        private static readonly SolidColorBrush IconLine = Views.Brush("#2A2A55");

        private (BarKind Kind, string? Resolution) _installed;
        private bool _loading, _touched, _dragging, _busy, _lettersBusy, _armiesLoading;
        private double _split = 0.5;
        private Task? _preview;
        private HotkeyService? _hotkeys;

        public AddonsSection()
        {
            InitializeComponent();
            Before.Source = OriginalArt;
            BeforeLabel.Text = Loc.T("ORIGINAL");

            _loading = true;
            foreach (var package in AddonService.Packages.Where(p => p.Kind == BarKind.Pro))
                Resolution.Items.Add(new ComboBoxItem { Content = Loc.Ltr(package.Resolution.Replace("x", " × ")), Tag = package.Resolution });
            _loading = false;

            foreach (string text in new[]
            {
                Loc.T("Checked with SHA-256 before anything is installed"),
                Loc.T("Only the .big archives are copied; scripts in the package are skipped"),
                Loc.T("Changes the interface only, so it never causes a mismatch"),
                Loc.T("Your previous bar is kept; Undo puts it back"),
                Loc.T("Source: TheSuperHackers/GeneralsControlBar on GitHub (MIT licence)"),
            })
                SafetyList.Children.Add(Note(text, Views.Passed));

            foreach (string text in new[]
            {
                Loc.T("Only pictures change, so it does not cause mismatches online."),
                Loc.T("The letters follow the hotkeys saved in the game. After you change hotkeys, press Update letters."),
                Loc.T("Every picture is backed up first. Turning it off puts the game's own pictures back."),
            })
                LettersNotes.Children.Add(Note(text, NoteDot));

            ReadInstalled();
            ShowLettersState();
        }

        public bool HasPendingChanges => Pending;

        public void OnShown()
        {
            ReadInstalled();
            ShowLettersState();
        }

        public Task ReadyAsync() => TabLetters.IsChecked == true ? EnsurePreview() : Task.CompletedTask;

        public void ShowPart(string part)
        {
            if (part is "letters" or "hotkeyletters")
                TabLetters.IsChecked = true;
            else
                TabBars.IsChecked = true;
        }

        private void View_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized)
                return;
            bool letters = TabLetters.IsChecked == true;
            BarsView.Visibility = BarsFooter.Visibility = letters ? Visibility.Collapsed : Visibility.Visible;
            LettersView.Visibility = LettersFooter.Visibility = letters ? Visibility.Visible : Visibility.Collapsed;
            if (letters)
            {
                ShowLettersState();
                _ = EnsurePreview();
            }
        }

        // ── Control bar ──

        private static BitmapSource Art(string name)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Assets/ControlBars/{name}.jpg");
            image.DecodePixelWidth = 1600;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            int top = (int)Math.Round(image.PixelHeight * CropTop);
            var crop = new CroppedBitmap(image, new Int32Rect(0, top, image.PixelWidth, image.PixelHeight - top));
            crop.Freeze();
            return crop;
        }

        private static string BarName(BarKind kind, string? resolution) => kind switch
        {
            BarKind.Pro => Loc.Ltr(resolution != null ? "Control Bar Pro " + resolution.Replace("x", " × ") : "Control Bar Pro"),
            BarKind.Other => Loc.T("Another control bar"),
            _ => Loc.T("Original bar"),
        };

        // Reads which bar is in the game folder; a choice the user made and has not applied yet stays
        private void ReadInstalled()
        {
            try
            {
                _installed = GamePaths.GameFound ? AddonService.Installed() : (BarKind.Original, null);
            }
            catch
            {
                _installed = (BarKind.Original, null);
            }
            OriginalInstalled.Visibility = _installed.Kind == BarKind.Original ? Visibility.Visible : Visibility.Collapsed;
            ProInstalled.Visibility = _installed.Kind == BarKind.Pro ? Visibility.Visible : Visibility.Collapsed;
            OtherNotice.Visibility = _installed.Kind == BarKind.Other ? Visibility.Visible : Visibility.Collapsed;
            if (_installed.Kind == BarKind.Other)
            {
                List<string> files;
                try
                {
                    files = AddonService.InstalledFiles().Select(System.IO.Path.GetFileName).OfType<string>().ToList();
                }
                catch
                {
                    files = new();
                }
                OtherDetail.Text = Loc.T("{0} in the game folder. Choose the original bar or Control Bar Pro to replace it; Undo puts it back.",
                    Loc.Ltr(string.Join(", ", files)));
            }
            if (!_touched || !Pending)
                SelectInstalled();
            Update();
        }

        // A bar that is not offered here leaves both choices empty until one is picked
        private void SelectInstalled()
        {
            _loading = true;
            ChoiceOriginal.IsChecked = _installed.Kind == BarKind.Original;
            ChoicePro.IsChecked = _installed.Kind == BarKind.Pro;
            string resolution;
            try
            {
                resolution = _installed.Resolution ?? AddonService.SuggestedResolution();
            }
            catch
            {
                resolution = "1920x1080";
            }
            Resolution.SelectedItem = Resolution.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == resolution);
            if (Resolution.SelectedItem == null)
                Resolution.SelectedIndex = 2;
            _loading = false;
            _touched = false;
        }

        private BarKind Chosen => ChoicePro.IsChecked == true ? BarKind.Pro : ChoiceOriginal.IsChecked == true ? BarKind.Original : BarKind.Other;
        private string? ChosenResolution => Chosen == BarKind.Pro ? (Resolution.SelectedItem as ComboBoxItem)?.Tag as string : null;

        private bool Pending => GamePaths.GameFound && (Chosen != _installed.Kind || (Chosen == BarKind.Pro && ChosenResolution != _installed.Resolution));

        private void Choice_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsInitialized)
                return;
            _touched = true;
            Update();
        }

        private void Resolution_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !IsInitialized)
                return;
            _touched = true;
            if (ChoicePro.IsChecked == true)
                Update();
            else
                ChoicePro.IsChecked = true;
        }

        private void Update()
        {
            bool pro = Chosen == BarKind.Pro;
            After.Source = ProArt;
            After.Visibility = Divider.Visibility = Handle.Visibility = AfterTag.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
            CompareBox.Cursor = pro ? Cursors.SizeWE : Cursors.Arrow;
            CompareNote.Text = pro
                ? Loc.T("Drag across the picture to compare. Pick the size that matches your game resolution; a wrong size looks stretched.")
                : Loc.T("Choose Control Bar Pro to compare it with the original.");
            ApplySplit();
            ShowChange();
        }

        private void ShowChange()
        {
            if (!GamePaths.GameFound)
            {
                ChangeText.Text = Loc.T("Folder not found");
                ChangeDetail.Text = Loc.T("Generals Online was not found. Start Command Center from the game folder or pass --game <folder>.");
                ApplyButton.IsEnabled = false;
                DiscardButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (_busy)
                return;

            bool pending = Pending;
            ApplyButton.IsEnabled = pending;
            DiscardButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            if (pending)
            {
                string arrow = Loc.IsRightToLeft ? "  ←  " : "  →  ";
                ChangeText.Text = BarName(_installed.Kind, _installed.Resolution) + arrow + BarName(Chosen, ChosenResolution);
                var package = AddonService.Package(Chosen, ChosenResolution);
                ChangeDetail.Text = package == null
                    ? Loc.T("Moves the add-on archives out of the game folder; Undo puts them back.")
                    : Loc.T("Downloads {0} from GitHub, checks it, then adds the .big files to the game folder.", Loc.Ltr(Views.Size(package.Size)));
            }
            else
            {
                ChangeText.Text = Loc.T("Installed now: {0}", BarName(_installed.Kind, _installed.Resolution));
                ChangeDetail.Text = Loc.T("Choose another bar to change it. The new bar shows the next time the game starts.");
            }
        }

        private void Discard_Click(object sender, RoutedEventArgs e)
        {
            SelectInstalled();
            Update();
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !Pending)
                return;
            var kind = Chosen;
            string? resolution = ChosenResolution;
            _busy = true;
            ApplyButton.IsEnabled = false;
            DiscardButton.IsEnabled = false;
            ChoiceOriginal.IsEnabled = ChoicePro.IsEnabled = false;
            ChangeText.Text = BarName(kind, resolution);
            var progress = new Progress<string>(s => ChangeDetail.Text = Plain(s));
            try
            {
                var made = await AddonService.ApplyAsync(kind, resolution, progress);
                _touched = false;
                Views.Main.Toast(Loc.T("Control bar changed. It shows the next time the game starts."), made.Count == 0 ? null : () =>
                {
                    try
                    {
                        AddonService.Undo(made);
                        Views.Main.Toast(Loc.T("Previous control bar put back"));
                    }
                    catch (Exception ex)
                    {
                        Views.Main.Toast(Loc.T("Something went wrong: {0}", Plain(ex.Message)), isError: true);
                    }
                    _touched = false;
                    ReadInstalled();
                    _ = AppState.RefreshHealthAsync();
                });
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Plain(ex.Message), isError: true);
            }
            finally
            {
                _busy = false;
                DiscardButton.IsEnabled = true;
                ChoiceOriginal.IsEnabled = ChoicePro.IsEnabled = true;
                ReadInstalled();
                _ = AppState.RefreshHealthAsync();
            }
        }

        private void Hd_Click(object sender, RoutedEventArgs e) => Views.Open(AddonService.HdPage);

        private void Source_Click(object sender, RoutedEventArgs e) => Views.Open(AddonService.RepoPage);

        // ── Before and after picture ──

        // The picture keeps the screenshots' shape: as wide as the panel, up to a height
        private void BarsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double width = BarsPanel.ActualWidth - 2;
            if (width <= 0 || !e.WidthChanged)
                return;
            double ratio = OriginalArt.PixelHeight / (double)OriginalArt.PixelWidth;
            double height = Math.Min(width * ratio, MaxPreviewHeight);
            CompareBox.Width = Math.Floor(height / ratio);
            CompareBox.Height = Math.Floor(height);
        }

        private void Compare_Down(object sender, MouseButtonEventArgs e)
        {
            if (Chosen != BarKind.Pro)
                return;
            _dragging = true;
            CompareBox.CaptureMouse();
            Compare_Move(sender, e);
        }

        private void Compare_Move(object sender, MouseEventArgs e)
        {
            if (!_dragging)
                return;
            _split = Math.Clamp(e.GetPosition(CompareBox).X / Math.Max(1, CompareBox.ActualWidth), 0, 1);
            ApplySplit();
        }

        private void Compare_Up(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            CompareBox.ReleaseMouseCapture();
        }

        private void Compare_LostCapture(object sender, MouseEventArgs e) => _dragging = false;

        private void Compare_SizeChanged(object sender, SizeChangedEventArgs e) => ApplySplit();

        private void ApplySplit()
        {
            double w = CompareBox.ActualWidth, h = CompareBox.ActualHeight;
            if (w <= 0)
                return;
            double x = Math.Round(w * _split);
            After.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, w - x), h));
            Divider.Margin = new Thickness(x - 1, 0, 0, 0);
            Handle.Margin = new Thickness(x - Handle.Width / 2, 0, 0, 0);
        }

        // ── Hotkey letters ──

        private void ShowLettersState()
        {
            bool on;
            try
            {
                on = IconLettersService.IsEnabled;
            }
            catch
            {
                on = false;
            }
            LettersOnButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            LettersOffButton.Visibility = LettersUpdateButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            LettersOnButton.IsEnabled = LettersOffButton.IsEnabled = LettersUpdateButton.IsEnabled = GamePaths.GameFound && !_lettersBusy;
            if (_lettersBusy)
                return;

            if (!GamePaths.GameFound)
            {
                LettersState.Text = Loc.T("Folder not found");
                LettersDetail.Text = Loc.T("Generals Online was not found. Start Command Center from the game folder or pass --game <folder>.");
                return;
            }
            LettersState.Text = on ? Loc.T("Hotkey letters are on") : Loc.T("Hotkey letters are off");
            LettersDetail.Text = on
                ? Loc.T("They show on the buttons the next time the game starts.")
                : Loc.T("Turning them on adds pictures with letters to the game folder. They show the next time the game starts.");
        }

        private void LettersOn_Click(object sender, RoutedEventArgs e) => TurnOn();

        private void LettersOff_Click(object sender, RoutedEventArgs e) => TurnOff();

        private void LettersUpdate_Click(object sender, RoutedEventArgs e) => _ = RunLettersAsync(async () =>
        {
            int count = await DrawLettersAsync();
            Views.Main.Toast(Loc.N(count, "Letters redrawn on {0} picture for your saved hotkeys.", "Letters redrawn on {0} pictures for your saved hotkeys."));
        });

        private void TurnOn() => _ = RunLettersAsync(async () =>
        {
            int count = await DrawLettersAsync();
            Views.Main.Toast(Loc.N(count, "Hotkey letters drawn on {0} picture. They show the next time the game starts.",
                "Hotkey letters drawn on {0} pictures. They show the next time the game starts."), TurnOff);
        });

        private void TurnOff() => _ = RunLettersAsync(async () =>
        {
            await Task.Run(IconLettersService.Remove);
            Views.Main.Toast(Loc.T("Hotkey letters removed. The game's own pictures are back."), TurnOn);
        });

        private static async Task<int> DrawLettersAsync()
        {
            var hotkeys = await AppState.Hotkeys() ?? throw new InvalidOperationException(Loc.T("The game's hotkeys could not be read."));
            return await Task.Run(() => IconLettersService.Apply(hotkeys));
        }

        private async Task RunLettersAsync(Func<Task> work)
        {
            if (_lettersBusy)
                return;
            _lettersBusy = true;
            ShowLettersState();
            LettersDetail.Text = Loc.T("Working…");
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Something went wrong: {0}", Plain(ex.Message)), isError: true);
            }
            finally
            {
                _lettersBusy = false;
                ShowLettersState();
            }
        }

        private Task EnsurePreview() => _preview ??= LoadPreviewAsync();

        private async Task LoadPreviewAsync()
        {
            PreviewMessage.Text = Loc.T("Loading the game's button pictures…");
            HotkeyService? hotkeys = null;
            try
            {
                hotkeys = GamePaths.GameFound ? await AppState.Hotkeys() : null;
            }
            catch { }
            if (hotkeys == null || hotkeys.Armies.Count == 0)
            {
                PreviewMessage.Text = Loc.T("The game's button pictures could not be read, so there is no preview.");
                return;
            }
            _hotkeys = hotkeys;
            _armiesLoading = true;
            foreach (var army in hotkeys.Armies)
                ArmyBox.Items.Add(new ComboBoxItem { Content = army.Name, Tag = army });
            ArmyBox.SelectedIndex = 0;
            _armiesLoading = false;
            ArmyBox.Visibility = Visibility.Visible;
            ShowPreview();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        private void Army_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_armiesLoading)
                ShowPreview();
        }

        // The builder's menu of the chosen army, as it looks now and with the letters
        private void ShowPreview()
        {
            if (_hotkeys is not { } hotkeys || (ArmyBox.SelectedItem as ComboBoxItem)?.Tag is not HotkeyArmy army)
                return;
            var menu = army.Menus.Where(m => m.Kind == MenuKind.Unit)
                .OrderByDescending(m => m.Buttons.Count(b => b.Action.Equals("DOZER_CONSTRUCT", StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(m => m.Buttons.Count(b => b.Image != null))
                .FirstOrDefault() ?? army.Menus.FirstOrDefault();
            if (menu == null)
                return;

            try
            {
                var buttons = menu.Buttons.Where(b => hotkeys.Images.Get(b.Image) != null)
                    .DistinctBy(b => b.Image, StringComparer.OrdinalIgnoreCase).Take(PreviewIcons).ToList();
                var lettered = IconLettersService.RenderPreview(hotkeys, buttons);
                BeforeIcons.Children.Clear();
                AfterIcons.Children.Clear();
                for (int i = 0; i < buttons.Count && i < lettered.Count; i++)
                {
                    string name = hotkeys.ShownName(buttons[i].Label);
                    BeforeIcons.Children.Add(Icon(hotkeys.Images.Get(buttons[i].Image)!, name));
                    AfterIcons.Children.Add(Icon(lettered[i], name));
                }
                PreviewTitle.Text = Loc.T("PREVIEW: {0}", menu.Name.ToUpperInvariant());
                PreviewMessage.Visibility = Visibility.Collapsed;
                PreviewRows.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                PreviewMessage.Text = Loc.T("Something went wrong: {0}", Plain(ex.Message));
                PreviewMessage.Visibility = Visibility.Visible;
                PreviewRows.Visibility = Visibility.Collapsed;
            }
        }

        private static Border Icon(BitmapSource image, string name)
        {
            var picture = new Image { Source = image, Width = 52, Height = 42, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return new Border
            {
                Child = picture,
                BorderBrush = IconLine,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = name,
            };
        }

        // ── Shared ──

        private static DockPanel Note(string text, Brush dot)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            var mark = new Ellipse { Width = 5, Height = 5, Fill = dot, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 9, 0) };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = NoteBrush, TextWrapping = TextWrapping.Wrap });
            return row;
        }

        // Messages from the services are written in English; in right-to-left mode they keep their own reading order
        private static string Plain(string text) =>
            Loc.IsRightToLeft && !text.Any(c => c is >= '؀' and <= 'ۿ') ? Loc.Ltr(text) : text;
    }
}
