using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages.Tools
{
    // Hotkey letters drawn on the button pictures (IconLettersService), with a before/after preview.
    // Shown as a tab of the Hotkeys section.
    public partial class HotkeyLettersPanel : UserControl
    {
        private const int PreviewIcons = 10;

        private static readonly SolidColorBrush NoteBrush = Views.Brush("#A8A8C8");
        private static readonly SolidColorBrush NoteDot = Views.Brush("#404070");
        private static readonly SolidColorBrush IconLine = Views.Brush("#2A2A55");

        private bool _lettersBusy, _armiesLoading;
        private Task? _preview;
        private HotkeyService? _hotkeys;

        public HotkeyLettersPanel()
        {
            InitializeComponent();
            foreach (string text in new[]
            {
                Loc.T("Only pictures change, so it does not cause mismatches online."),
                Loc.T("The letters follow the hotkeys saved in the game. After you change hotkeys, press Update letters."),
                Loc.T("Every picture is backed up first. Turning it off puts the game's own pictures back."),
            })
                LettersNotes.Children.Add(Note(text, NoteDot));
            Refresh();
        }

        // On or off, read again from the game folder
        public void Refresh()
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
            LettersOnButton.IsEnabled = LettersOffButton.IsEnabled = LettersUpdateButton.IsEnabled = GamePaths.ZeroHourFound && !_lettersBusy;
            if (_lettersBusy)
                return;

            if (!GamePaths.ZeroHourFound)
            {
                LettersState.Text = Loc.T("Folder not found");
                LettersDetail.Text = Loc.T("Zero Hour was not found. Press Play on the home page to choose the game folder.");
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
            Refresh();
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
                Refresh();
            }
        }

        public Task EnsurePreview() => _preview ??= LoadPreviewAsync();

        private async Task LoadPreviewAsync()
        {
            PreviewMessage.Text = Loc.T("Loading the game's button pictures…");
            HotkeyService? hotkeys = null;
            try
            {
                hotkeys = GamePaths.ZeroHourFound ? await AppState.Hotkeys() : null;
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
        private static string Plain(string text)
        {
            text = Loc.T(text);
            return Loc.IsRightToLeft && !text.Any(c => c is >= '؀' and <= 'ۿ') ? Loc.Ltr(text) : text;
        }
    }
}
