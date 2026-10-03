using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages.Tools
{
    // A menu (building, unit or rank of powers) in the list of the Buttons view
    public sealed class HotkeyMenuRow : INotifyPropertyChanged
    {
        public required HotkeyMenu Menu { get; init; }
        public required string Name { get; init; }
        public required string Group { get; init; }

        private Brush? _mark;
        private string? _markTip;

        public Brush? Mark
        {
            get => _mark;
            set { _mark = value; Changed(nameof(Mark)); Changed(nameof(MarkVisibility)); }
        }

        public string? MarkTip
        {
            get => _markTip;
            set { _markTip = value; Changed(nameof(MarkTip)); }
        }

        public Visibility MarkVisibility => _mark == null ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // A row of the Game keys view: one command, or one of the four groups of team keys
    public sealed class GameKeyRow
    {
        public GameKey? Key { get; init; }
        public string? TeamGroup { get; init; }
        public required string Group { get; init; }
        public required string Name { get; init; }
        public required string Shortcut { get; init; }
        public required Brush NameBrush { get; init; }
        public required Brush CapBrush { get; init; }
        public Brush? Mark { get; init; }
        public string? MarkTip { get; init; }
        public Visibility MarkVisibility => Mark == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Hotkeys: the letter of every unit and building button (generals.csf) and the game's own keys (CommandMap.ini).
    // Edits stay pending until Save, which writes loose files through BackupService so they can be undone.
    public partial class HotkeysSection : UserControl, IToolSection
    {
        private static readonly SolidColorBrush Red = Views.Problem;
        private static readonly SolidColorBrush Amber = Views.Warning;
        private static readonly SolidColorBrush Green = Views.Passed;
        private static readonly SolidColorBrush Blue = Views.Blue;
        private static readonly SolidColorBrush LightBlue = Views.Brush("#5BA4FF");
        private static readonly SolidColorBrush Muted = Views.Hint;
        private static readonly SolidColorBrush Soft = Views.Brush("#BCC2D8");
        private static readonly SolidColorBrush Line = Views.Brush("#26FFFFFF");
        private static readonly SolidColorBrush Page = Views.Brush("#0A0E20");
        private static readonly SolidColorBrush SelectedFill = Views.Brush("#262980FF");
        private static readonly SolidColorBrush SlotFill = Views.Brush("#0A0E20");
        private static readonly SolidColorBrush SlotLine = Views.Brush("#1AFFFFFF");
        private static readonly SolidColorBrush BadgeFill = Views.Brush("#E0000000");
        private static readonly SolidColorBrush White = Brushes.White;

        // One command bar slot before scaling; the bar is two (sometimes three) rows of seven
        private const double TileWidth = 64, TileHeight = 51, TileGap = 2;

        private HotkeyService? _hk;
        private HotkeyArmy? _army;
        private HotkeyMenu? _menu;
        private HotkeyButton? _button;
        private GameKey? _gameKey;
        private string? _teamGroup;
        private string? _keepLabel;
        private bool _listening, _keyListening, _filling;
        private string? _listenHint;
        private List<HotkeyMenuRow> _menuRows = new();
        private ListCollectionView? _keyView;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HotkeysSection()
        {
            InitializeComponent();
            SizeChanged += (_, _) => Fit();
            _ = LoadAsync();
        }

        // Only the user's own edits hold the window open; a team key repair just waits for the next Save
        public bool HasPendingChanges => _hk is { DiscardableCount: > 0 };

        public void OnShown()
        {
            UpdateStatus();
            LettersView.Refresh();
        }

        public async Task ReadyAsync()
        {
            await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(40)));
            if (TabLetters.IsChecked == true)
                await LettersView.EnsurePreview();
        }

        public void ShowPart(string part)
        {
            switch (part.ToLowerInvariant())
            {
                case "gamekeys":
                case "keys":
                    TabKeys.IsChecked = true;
                    break;
                case "letters":
                case "hotkeyletters":
                    TabLetters.IsChecked = true;
                    break;
                case "buttons":
                    TabButtons.IsChecked = true;
                    break;
            }
        }

        private async Task LoadAsync()
        {
            try
            {
                _hk = await AppState.Hotkeys();
                // The first pictures come from large atlases; decode them away from the window
                if (_hk != null)
                    await RunSta(() => Preload(_hk));
            }
            catch (Exception)
            {
                _hk = null;
            }

            if (_hk == null)
            {
                LoadingText.Text = Loc.T("Could not read the game's hotkeys. Check that Command Center points at the game folder.");
                _ready.TrySetResult();
                return;
            }

            LoadingText.Visibility = Visibility.Collapsed;
            FillArmies();
            FillGameKeys();
            ShowView();
            UpdateStatus();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            _ready.TrySetResult();
        }

        private static void Preload(HotkeyService hk)
        {
            foreach (string? image in hk.Armies.SelectMany(a => a.Menus).SelectMany(m => m.Buttons.Concat(m.Silent)).Select(b => b.Image).Distinct())
                hk.Images.Get(image);
        }

        private static Task RunSta(Action work)
        {
            var done = new TaskCompletionSource();
            var thread = new Thread(() =>
            {
                try
                {
                    work();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return done.Task;
        }

        // Narrow windows keep the lists slim and put the selected button under the bar; wide ones put it beside the bar.
        // The bar grows on larger windows, but not so far that the game's small pictures turn blurry.
        private void Fit()
        {
            bool wide = ActualWidth >= 980;
            MenuColumn.Width = new GridLength(wide ? 230 : 190);
            DetailColumn.Width = new GridLength(wide ? 320 : 250);

            double natural = (TileWidth + 2 * TileGap) * 7;
            bool side = ActualWidth - MenuColumn.Width.Value - 12 >= 1000;
            BarView.MaxWidth = natural * (side ? 1.3 : ActualHeight >= 560 ? 1.45 : 1.0);
            BarColumn.Width = side ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            GapColumn.Width = new GridLength(side ? 12 : 0);
            EditColumn.Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(EditBox, side ? 0 : 2);
            Grid.SetRowSpan(EditBox, side ? 3 : 1);
            Grid.SetColumn(EditBox, side ? 2 : 0);
        }

        // ── Views ──

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (ButtonsView == null)
                return;
            StopListening();
            ShowView();
            ShowEditor();
            ShowGameKey();
        }

        private void ShowView()
        {
            bool keys = TabKeys.IsChecked == true, letters = TabLetters.IsChecked == true, loaded = _hk != null;
            ButtonsView.Visibility = loaded && !keys && !letters ? Visibility.Visible : Visibility.Collapsed;
            KeysView.Visibility = loaded && keys ? Visibility.Visible : Visibility.Collapsed;
            LettersView.Visibility = letters ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Visibility = !loaded && !letters ? Visibility.Visible : Visibility.Collapsed;
            LayoutButtons.Visibility = loaded && !letters ? Visibility.Visible : Visibility.Collapsed;
            GridButton.Visibility = keys ? Visibility.Collapsed : Visibility.Visible;
            if (letters)
            {
                LettersView.Refresh();
                _ = LettersView.EnsurePreview();
            }
        }

        // ── Armies and menus ──

        private void FillArmies()
        {
            if (_hk == null)
                return;
            string? keep = _army?.Name;
            _filling = true;
            ArmyBox.Items.Clear();
            foreach (var army in _hk.Armies)
            {
                ArmyBox.Items.Add(new ComboBoxItem
                {
                    Content = Loc.T(army.Name),
                    Tag = army,
                    FontWeight = army.General == null ? FontWeights.SemiBold : FontWeights.Normal,
                    Padding = new Thickness(army.General == null ? 8 : 20, 4, 8, 4),
                });
            }
            _filling = false;
            int index = _hk.Armies.FindIndex(a => a.Name == keep);
            ArmyBox.SelectedIndex = -1;
            ArmyBox.SelectedIndex = Math.Max(0, index);
        }

        private void Army_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || ArmyBox.SelectedItem is not ComboBoxItem { Tag: HotkeyArmy army })
                return;
            _army = army;
            FillMenus();
        }

        private static string MenuName(HotkeyMenu menu)
        {
            if (menu.Kind != MenuKind.Powers)
                return menu.Name;
            var rank = Regex.Match(menu.SetName, @"(\d+)$");
            return rank.Success ? Loc.T("Rank {0} powers", rank.Value) : menu.Name;
        }

        // Keeps the same menu and button when they exist in the new army
        private void FillMenus()
        {
            if (_hk == null || _army == null)
                return;
            string? keepMenu = _menu?.Name;
            _keepLabel = _button?.Label;
            _menuRows = _army.Menus.Select(m => new HotkeyMenuRow
            {
                Menu = m,
                Name = MenuName(m),
                Group = m.Kind switch { MenuKind.Structure => Loc.T("STRUCTURES"), MenuKind.Unit => Loc.T("UNITS"), _ => Loc.T("GENERAL'S POWERS") },
            }).ToList();
            RefreshMenuMarks();

            var view = new ListCollectionView(_menuRows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HotkeyMenuRow.Group)));
            _menu = null;
            MenuList.ItemsSource = view;
            var select = _menuRows.FirstOrDefault(r => r.Menu.Name == keepMenu)
                         ?? _menuRows.FirstOrDefault(r => r.Menu.Name.Contains("War Factory", StringComparison.OrdinalIgnoreCase))
                         ?? _menuRows.FirstOrDefault();
            MenuList.SelectedItem = select;
            if (select != null)
                MenuList.ScrollIntoView(select);
            _keepLabel = null;
        }

        private void RefreshMenuMarks()
        {
            if (_hk == null)
                return;
            foreach (var row in _menuRows)
            {
                var worst = row.Menu.Buttons.Select(b => _hk.WorstIssue(row.Menu, b)).DefaultIfEmpty(IssueLevel.Ok).Max();
                row.Mark = worst switch { IssueLevel.Error => Red, IssueLevel.Warn => Amber, _ => null };
                row.MarkTip = worst switch
                {
                    IssueLevel.Error => Loc.T("Two buttons share a key"),
                    IssueLevel.Warn => Loc.T("A key is also a game key"),
                    _ => null,
                };
            }
        }

        private void Menu_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (MenuList.SelectedItem is not HotkeyMenuRow row || row.Menu == _menu)
                return;
            _menu = row.Menu;
            _button = (_keepLabel != null ? _menu.Buttons.FirstOrDefault(b => b.Label.Equals(_keepLabel, StringComparison.OrdinalIgnoreCase)) : null)
                      ?? _menu.Buttons.FirstOrDefault();
            StopListening();
            ShowSlots();
            ShowEditor();
        }

        // ── Command bar ──

        private void ShowSlots()
        {
            Slots.Children.Clear();
            if (_hk == null || _menu == null)
                return;
            int count = Math.Max(14, (int)Math.Ceiling(_menu.MaxSlot / 7.0) * 7);
            for (int slot = 1; slot <= count; slot++)
            {
                var button = _menu.Buttons.FirstOrDefault(b => b.Slot == slot);
                var silent = button == null ? _menu.Silent.FirstOrDefault(b => b.Slot == slot) : null;
                Slots.Children.Add(Tile(button, silent));
            }
        }

        private FrameworkElement Tile(HotkeyButton? button, HotkeyButton? silent)
        {
            var tile = new Grid { Width = TileWidth, Height = TileHeight, Margin = new Thickness(TileGap) };
            tile.Children.Add(new Border { Background = SlotFill, BorderBrush = SlotLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) });
            var shown = button ?? silent;
            if (shown == null)
                return tile;

            var image = new Border
            {
                Background = _hk!.Images.Get(shown.Image) is { } picture ? new ImageBrush(picture) { Stretch = Stretch.UniformToFill } : null,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(1),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            tile.Children.Add(image);
            if (button == null)
            {
                // Buttons without text cannot have a key (science purchases and the like)
                image.Opacity = 0.35;
                tile.ToolTip = Loc.T("{0} · no hotkey", shown.Name);
                return tile;
            }

            char key = _hk.KeyFor(button.Label);
            bool clash = _hk.ButtonIssues(_menu!, button).Any(i => i.Level == IssueLevel.Error);
            bool shared = _hk.SharedGameKey(button) != null;
            bool selected = button == _button;

            tile.Children.Add(new Border
            {
                Background = BadgeFill,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(5),
                Child = new TextBlock
                {
                    Text = key == '\0' ? "–" : key.ToString(),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = clash ? Red : key == '\0' ? Muted : White,
                },
            });
            if (_hk.IsChanged(button.Label))
                tile.Children.Add(Dot(LightBlue, HorizontalAlignment.Left));
            if (shared)
                tile.Children.Add(Dot(Amber, HorizontalAlignment.Right));

            Brush rest = selected ? Blue : clash ? Red : Brushes.Transparent;
            var frame = new Border { BorderThickness = new Thickness(2), BorderBrush = rest, CornerRadius = new CornerRadius(8) };
            tile.Children.Add(frame);

            tile.ToolTip = key == '\0' ? Loc.T("{0} · no key", button.Name) : Loc.T("{0} · key {1}", button.Name, key);
            tile.Cursor = Cursors.Hand;
            tile.MouseEnter += (_, _) => frame.BorderBrush = selected ? Blue : clash ? Red : LightBlue;
            tile.MouseLeave += (_, _) => frame.BorderBrush = rest;
            tile.MouseLeftButtonDown += (_, e) =>
            {
                SelectButton(button);
                if (e.ClickCount == 2)
                    StartListening();
            };
            return tile;
        }

        private static Ellipse Dot(Brush fill, HorizontalAlignment side) => new()
        {
            Width = 8,
            Height = 8,
            Fill = fill,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
        };

        private void SelectButton(HotkeyButton button)
        {
            _button = button;
            StopListening();
            ShowSlots();
            ShowEditor();
        }

        // ── Selected button ──

        private void ShowEditor()
        {
            if (_hk == null || _menu == null || _button == null)
                return;
            var b = _button;
            BigIcon.Source = _hk.Images.Get(b.Image);
            ButtonName.Text = b.Name;
            ButtonWhere.Text = Loc.T("Slot {0} · {1}", b.Slot, MenuName(_menu));
            char key = _hk.KeyFor(b.Label), def = _hk.DefaultKeyFor(b.Label);

            if (_listening)
            {
                KeyCapText.Text = "?";
                KeyCapText.Foreground = LightBlue;
                KeyCap.Background = SelectedFill;
                KeyCap.BorderBrush = Blue;
                KeyHint.Text = _listenHint ?? Loc.T("Press a letter A–Z or a number. Esc cancels.");
                KeyHint.Foreground = LightBlue;
            }
            else
            {
                KeyCapText.Text = key == '\0' ? "–" : key.ToString();
                KeyCapText.Foreground = key == '\0' ? Muted : White;
                KeyCap.Background = Page;
                KeyCap.BorderBrush = _hk.IsChanged(b.Label) ? LightBlue : Line;
                KeyHint.Foreground = Soft;
                KeyHint.Text = key == def
                    ? def == '\0' ? Loc.T("The game gives this button no key. Pick one to add it.") : Loc.T("The game's default key.")
                    : def == '\0' ? Loc.T("Added by you. The game gives this button no key.") : Loc.T("Changed from the default {0}.", def);
            }

            DefaultButton.IsEnabled = key != def;
            DefaultButton.ToolTip = Loc.T("Back to the default ({0})", def == '\0' ? Loc.T("no key") : def.ToString());
            ClearButton.IsEnabled = key != '\0';

            Issues.Children.Clear();
            foreach (var issue in _hk.ButtonIssues(_menu, b))
                Issues.Children.Add(IssueRow(issue));
            Issues.Visibility = Issues.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            FreeKeys.Children.Clear();
            foreach (char c in _hk.SuggestKeys(_menu, b, 6))
            {
                var chip = new Button { Content = c.ToString(), Style = (Style)FindResource("KeyChip"), ToolTip = Loc.T("Use {0}", c) };
                char pick = c;
                chip.Click += (_, _) => SetButtonKey(pick);
                FreeKeys.Children.Add(chip);
            }
            if (FreeKeys.Children.Count == 0)
                FreeKeys.Children.Add(new TextBlock { Text = Loc.T("None left in this menu"), FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });

            var menus = _hk.MenusUsing(b.Label).ToList();
            int others = menus.Count - 1;
            SameText.Visibility = others > 0 ? Visibility.Visible : Visibility.Collapsed;
            SameText.Text = Loc.N(others, "Also in 1 other menu; the key changes there too.", "Also in {0} other menus; the key changes there too.");
            SameText.ToolTip = others > 0
                ? string.Join("\n", menus.Take(14).Select(m => Loc.T("{0} · {1}", MenuName(m.Menu), Loc.T(m.Army.Name))))
                  + (menus.Count > 14 ? "\n" + Loc.T("and {0} more", menus.Count - 14) : "")
                : null;
        }

        private static FrameworkElement IssueRow(KeyIssue issue)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = issue.Level switch { IssueLevel.Error => Red, IssueLevel.Warn => Amber, IssueLevel.Info => LightBlue, _ => Green },
                Margin = new Thickness(0, 5, 9, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(dot, Dock.Left);
            dock.Children.Add(dot);
            dock.Children.Add(new TextBlock
            {
                Text = issue.Text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = issue.Level == IssueLevel.Ok ? Soft : White,
            });
            return dock;
        }

        private void SetButtonKey(char key)
        {
            if (_hk == null || _button == null)
                return;
            _hk.SetKey(_button.Label, key);
            RefreshAll();
        }

        private void KeyCap_Click(object sender, MouseButtonEventArgs e) => StartListening();

        private void Change_Click(object sender, RoutedEventArgs e) => StartListening();

        private void ResetKey_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null || _button == null)
                return;
            _hk.ResetKey(_button.Label);
            RefreshAll();
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => SetButtonKey('\0');

        // ── Key capture ──

        private void StartListening()
        {
            if (_button == null)
                return;
            _keyListening = false;
            _listening = true;
            _listenHint = null;
            ShowEditor();
            Keyboard.Focus(this);
        }

        private void StartKeyListening()
        {
            if (_gameKey is not { CanChange: true })
                return;
            _listening = false;
            _keyListening = true;
            _listenHint = null;
            ShowGameKey();
            Keyboard.Focus(this);
        }

        private void StopListening()
        {
            _listening = false;
            _keyListening = false;
            _listenHint = null;
        }

        // Clicking anywhere else ends the capture
        private void Section_FocusChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsKeyboardFocused || !(_listening || _keyListening))
                return;
            StopListening();
            ShowEditor();
            ShowGameKey();
        }

        private void Section_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_listening && !_keyListening)
                return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool modifier = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                StopListening();
                ShowEditor();
                ShowGameKey();
                return;
            }

            if (_listening)
            {
                char c = key >= Key.A && key <= Key.Z ? (char)('A' + (key - Key.A))
                       : key >= Key.D0 && key <= Key.D9 ? (char)('0' + (key - Key.D0))
                       : key >= Key.NumPad0 && key <= Key.NumPad9 ? (char)('0' + (key - Key.NumPad0))
                       : '\0';
                if (modifier || (c != '\0' && Keyboard.Modifiers != ModifierKeys.None))
                    _listenHint = Loc.T("Button keys cannot use Ctrl, Alt or Shift; the game ignores them. Press the letter alone.");
                else if (c == '\0')
                    _listenHint = Loc.T("Only a letter A–Z or a number, pressed alone, works for buttons.");
                else
                {
                    StopListening();
                    SetButtonKey(c);
                    return;
                }
                ShowEditor();
                return;
            }

            if (_hk == null || _gameKey == null || modifier)
                return;
            string? name = HotkeyService.GameKeyName(key);
            if (name == null)
            {
                _listenHint = Loc.T("The game cannot use that key. Try a letter, number, F-key or numpad key.");
                ShowGameKey();
                return;
            }
            if (HotkeyService.CannotUse(_gameKey, name) is { } reason)
            {
                _listenHint = reason;
                ShowGameKey();
                return;
            }
            var mods = Keyboard.Modifiers;
            _hk.SetGameKey(_gameKey, name, HotkeyService.ModifiersName(mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Shift)));
            StopListening();
            RefreshAll();
        }

        // ── Game keys ──

        private static readonly string[] CategoryOrder = { "SELECTION", "CONTROL", "INTERFACE", "CAMERA", "CHAT", "BUILT-IN" };

        private static readonly (string Group, string Name)[] TeamGroups =
        {
            ("SELECT", "Select a team"), ("CREATE", "Make a team"), ("ADD", "Add to a team"), ("VIEW", "Jump to a team"),
        };

        private static string GroupName(GameKey k) => k.Category switch
        {
            "SELECTION" => Loc.T("SELECTION"),
            "CONTROL" => Loc.T("UNIT ORDERS"),
            "INTERFACE" => Loc.T("INTERFACE"),
            "CAMERA" => Loc.T("CAMERA"),
            "CHAT" => Loc.T("CHAT AND BEACONS"),
            "BUILT-IN" => Loc.T("BUILT INTO THE GAME"),
            _ => k.Category,
        };

        // Team keys come first as four read-only rows; every other command can be picked and changed
        private void FillGameKeys()
        {
            if (_hk == null)
                return;
            var rows = new List<GameKeyRow>();
            foreach (var (group, name) in TeamGroups)
            {
                var keys = _hk.GameKeys.Where(k => k.IsTeam && HotkeyService.TeamGroupOf(k) == group).ToList();
                if (keys.Count == 0)
                    continue;
                int changed = keys.Count(k => k.Pending);
                rows.Add(new GameKeyRow
                {
                    TeamGroup = group,
                    Group = Loc.T("TEAM KEYS"),
                    Name = Loc.T(name),
                    Shortcut = TeamShortcut(keys[0]),
                    NameBrush = Muted,
                    CapBrush = Line,
                    Mark = changed > 0 ? Amber : null,
                    MarkTip = changed > 0 ? Loc.N(changed, "Changed earlier; Save puts it back", "{0} changed earlier; Save puts them back") : null,
                });
            }

            foreach (var k in _hk.ListedGameKeys.OrderBy(k => Array.IndexOf(CategoryOrder, k.Category) is int i && i >= 0 ? i : 50).ThenBy(k => k.Order))
            {
                var issues = _hk.GameKeyIssues(k);
                var worst = issues.Select(i => i.Level).DefaultIfEmpty(IssueLevel.Ok).Max();
                rows.Add(new GameKeyRow
                {
                    Key = k,
                    Group = GroupName(k),
                    Name = k.Name,
                    Shortcut = Loc.Ltr(k.Shortcut),
                    NameBrush = k.CanChange ? White : Muted,
                    CapBrush = k.Pending ? Blue : Line,
                    Mark = worst == IssueLevel.Error ? Red : worst == IssueLevel.Warn ? Amber : k.Changed ? LightBlue : null,
                    MarkTip = issues.FirstOrDefault(i => i.Level >= IssueLevel.Warn)?.Text ?? (k.Changed ? Loc.T("Changed from default") : null),
                });
            }

            var keep = rows.FirstOrDefault(r => _teamGroup != null ? r.TeamGroup == _teamGroup : r.Key != null && r.Key.Command == _gameKey?.Command)
                       ?? rows.FirstOrDefault(r => r.Key is { CanChange: true });
            _keyView = new ListCollectionView(rows);
            _keyView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GameKeyRow.Group)));
            _keyView.Filter = o => o is GameKeyRow r && Matches(r);
            KeyList.ItemsSource = _keyView;
            KeyList.SelectedItem = keep;

            int teams = _hk.ChangedTeamKeys;
            TeamNotice.Visibility = teams > 0 ? Visibility.Visible : Visibility.Collapsed;
            TeamNoticeText.Text = Loc.N(teams,
                "1 team key was changed in CommandMap.ini earlier. Team keys always use the game's defaults; Save puts it back.",
                "{0} team keys were changed in CommandMap.ini earlier. Team keys always use the game's defaults; Save puts them back.");
        }

        private static string TeamShortcut(GameKey first) => Loc.Ltr(HotkeyService.Describe("", first.OriginalModifiers) + "0–9");

        private bool Matches(GameKeyRow row)
        {
            string q = KeySearch.Text.Trim();
            return q.Length == 0 || row.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || row.Shortcut.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private void KeySearch_Changed(object sender, TextChangedEventArgs e)
        {
            KeySearchHint.Visibility = KeySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _keyView?.Refresh();
        }

        private void KeyList_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (KeyList.SelectedItem is not GameKeyRow row)
                return;
            _gameKey = row.Key;
            _teamGroup = row.TeamGroup;
            if (_keyListening)
                StopListening();
            ShowGameKey();
        }

        private void ShowGameKey()
        {
            if (_hk == null)
                return;
            KIssues.Children.Clear();

            if (_teamGroup != null)
            {
                var first = _hk.GameKeys.FirstOrDefault(k => k.IsTeam && HotkeyService.TeamGroupOf(k) == _teamGroup);
                KCategory.Text = Loc.T("TEAM KEYS");
                KName.Text = Loc.T(TeamGroups.FirstOrDefault(g => g.Group == _teamGroup).Name ?? "");
                KCapText.Text = first != null ? TeamShortcut(first) : "";
                KCapText.Foreground = Muted;
                KCap.Background = Page;
                KCap.BorderBrush = Line;
                KCap.Cursor = null;
                KHint.Foreground = Soft;
                KHint.Text = Loc.T("Team keys always stay on the game's defaults, so they cannot be changed here. Every other key is checked against them, so nothing you set takes their place.");
                KButtons.Visibility = Visibility.Collapsed;
                int teams = _hk.ChangedTeamKeys;
                if (teams > 0)
                    KIssues.Children.Add(IssueRow(new KeyIssue(IssueLevel.Warn, Loc.N(teams,
                        "1 team key was changed earlier. Save puts it back to the game's default.",
                        "{0} team keys were changed earlier. Save puts them back to the game's defaults."))));
                return;
            }

            if (_gameKey == null)
                return;
            var k = _gameKey;
            KButtons.Visibility = Visibility.Visible;
            KCategory.Text = GroupName(k);
            KName.Text = k.Name;
            KChange.IsEnabled = k.CanChange;
            KReset.IsEnabled = k.CanChange && k.Changed;
            KReset.ToolTip = Loc.T("Back to the default ({0})", Loc.Ltr(k.DefaultShortcut));
            KCap.Cursor = k.CanChange ? Cursors.Hand : null;
            KCap.ToolTip = k.CanChange ? Loc.T("Click, then press the new combination") : null;

            if (_keyListening)
            {
                KCapText.Text = Loc.T("Press the new keys…");
                KCapText.Foreground = LightBlue;
                KCap.Background = SelectedFill;
                KCap.BorderBrush = Blue;
                KHint.Foreground = LightBlue;
                KHint.Text = _listenHint ?? Loc.T("Hold Ctrl, Alt or Shift if you want them, then press the key. Esc cancels.");
            }
            else
            {
                KCapText.Text = Loc.Ltr(k.Shortcut);
                KCapText.Foreground = k.CanChange ? White : Muted;
                KCap.Background = Page;
                KCap.BorderBrush = k.Pending ? Blue : Line;
                KHint.Foreground = Soft;
                KHint.Text = k.IsFixed ? Loc.T("The game adds this key by itself and cannot change it. It is listed so you do not pick it for something else.")
                    : k.Changed ? Loc.T("Changed from {0}.", Loc.Ltr(k.DefaultShortcut)) + (k.IsBuiltIn ? " " + Loc.T("Only Generals Online reads this change.") : "")
                    : k.IsBuiltIn ? Loc.T("The game adds this key by itself.")
                    : Loc.T("The game's default.");
            }

            var issues = _hk.GameKeyIssues(k);
            if (issues.Count == 0 && k.CanChange)
                issues.Add(new KeyIssue(IssueLevel.Ok, Loc.T("No other command uses this combination.")));
            foreach (var issue in issues)
                KIssues.Children.Add(IssueRow(issue));
        }

        private void KCap_Click(object sender, MouseButtonEventArgs e) => StartKeyListening();

        private void KChange_Click(object sender, RoutedEventArgs e) => StartKeyListening();

        private void KReset_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null || _gameKey == null)
                return;
            _hk.ResetGameKey(_gameKey);
            RefreshAll();
        }

        // ── Layouts ──

        private void Grid_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null)
                return;
            _hk.ApplyGridLayout();
            RefreshAll();
            Views.Main.Toast(Loc.T("Grid layout loaded. Check the marked menus, then press Save."));
        }

        private void Defaults_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null)
                return;
            _hk.ApplyClassicLayout();
            _hk.ResetGameKeys();
            RefreshAll();
            Views.Main.Toast(Loc.T("Game defaults loaded. Press Save to keep them."));
        }

        // ── Pending changes ──

        // After an edit: same objects, new keys
        private void RefreshAll()
        {
            StopListening();
            RefreshMenuMarks();
            ShowSlots();
            ShowEditor();
            FillGameKeys();
            UpdateStatus();
        }

        // After Save, Undo or a reload the service holds new objects; find the same army, menu, button and command again
        private void Reloaded()
        {
            StopListening();
            if (_hk == null)
                return;
            _gameKey = _hk.GameKeys.FirstOrDefault(k => k.Command == _gameKey?.Command);
            FillArmies();
            FillGameKeys();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_hk == null)
                return;
            int pending = _hk.PendingCount, discardable = _hk.DiscardableCount, teams = _hk.ChangedTeamKeys;
            SaveButton.IsEnabled = pending > 0;
            DiscardButton.Visibility = discardable > 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusDot.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (pending == 0)
            {
                StatusText.Text = Loc.T("Hotkeys never cause a mismatch online");
                StatusText.Foreground = Muted;
                StatusText.ToolTip = Loc.T("Hotkeys live in generals.csf and CommandMap.ini, which are not part of the multiplayer check.");
                SaveButton.ToolTip = null;
                return;
            }

            StatusText.Text = discardable > 0
                ? Loc.N(discardable, "{0} change not saved", "{0} changes not saved")
                : Loc.N(teams, "1 team key to put back", "{0} team keys to put back");
            StatusText.Foreground = White;
            string files = Loc.List(_hk.PendingFiles().Select(f => Loc.Ltr(System.IO.Path.GetFileName(f))));
            string where = Loc.T("Will be saved to {0}. A backup is kept.", files);
            StatusText.ToolTip = teams > 0 && discardable > 0
                ? where + " " + Loc.N(teams, "1 team key goes back to the game's default.", "{0} team keys go back to the game's defaults.")
                : where;
            SaveButton.ToolTip = where;
        }

        private void Discard_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null)
                return;
            _hk.DiscardPending();
            RefreshAll();
        }

        private void Save_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            if (_hk == null || _hk.PendingCount == 0)
                return;
            if (GameLauncher.IsGameRunning())
            {
                Views.Main.Toast(Loc.T("Close the game first; it reads hotkeys only when it starts."), isError: true);
                return;
            }

            var hk = _hk;
            string files = Loc.List(hk.PendingFiles().Select(f => Loc.Ltr(System.IO.Path.GetFileName(f))));
            int before = BackupService.Load().Count;
            try
            {
                hk.Save();
                string message = Loc.T("Saved to {0}. The new keys work the next time the game starts.", files);
                try
                {
                    // The letters drawn on the button pictures follow the saved keys
                    IconLettersService.RefreshIfEnabled(hk);
                }
                catch (Exception ex)
                {
                    message += " " + Loc.T("The letters on the icons could not be updated: {0}", ex.Message);
                }
                var made = BackupService.Load().Skip(before).ToList();
                Views.Main.Toast(message, made.Count == 0 ? null : () =>
                {
                    foreach (var entry in Enumerable.Reverse(made))
                        BackupService.Undo(entry);
                    hk.Reload();
                    Reloaded();
                    Views.Main.Toast(Loc.T("Hotkeys put back"));
                });
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Could not save: {0}", ex.Message), isError: true);
            }
            Reloaded();
        }
    }
}
