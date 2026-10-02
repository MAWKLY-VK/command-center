using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public sealed class MenuItemView
    {
        public required HotkeyMenu Menu { get; init; }
        public string Name => Menu.Name;
        public string Count => Menu.Buttons.Count.ToString();
        public Mark Mark { get; set; }
        public string? MarkTip { get; set; }
        public string Group => Menu.Kind switch { MenuKind.Structure => "Structures", MenuKind.Unit => "Units", _ => "General's powers" };
    }

    public sealed class GameKeyView
    {
        public required GameKey Key { get; init; }
        public required string Group { get; init; }
        public string Name => Key.Name;
        public string Shortcut => Key.Shortcut;
        public string Default => Key.CanChange ? "Default: " + Key.DefaultShortcut : "Built in · read only";
        public Visibility DefaultVisibility => Key.Changed || !Key.CanChange ? Visibility.Visible : Visibility.Collapsed;
        public Mark Mark { get; set; }
        public string? MarkTip { get; set; }
        public Brush CapBorder => Key.Pending ? Views.Res("Info") : Views.Res("LineHi");
        public Brush CapText => Key.CanChange ? Views.Res("Text") : Views.Res("Text3");
    }

    public partial class HotkeysPage : UserControl, IPage
    {
        private HotkeyService? _hk;
        private HotkeyArmy? _army;
        private HotkeyMenu? _menu;
        private HotkeyButton? _button;
        private GameKey? _gameKey;
        private bool _listening, _keyListening, _filling;
        private readonly DockPanel _footer = new();
        private readonly TextBlock _pendingText = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _whereText = new() { FontSize = 13, Foreground = Views.Res("Text2"), VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _topRight;
        private ListCollectionView? _keyView;

        public HotkeysPage()
        {
            InitializeComponent();
            _topRight = Views.Chip("shield", "Never causes a mismatch", Mark.Ok);
            _topRight.ToolTip = "Hotkeys live in generals.csf and CommandMap.ini, which are not part of the multiplayer check";
            BuildFooter();
            SizeChanged += (_, _) =>
            {
                // Narrow windows (1280 × 720, or 150% scaling) give the editor more room
                bool narrow = ActualWidth < 1250;
                MenuCol.Width = new GridLength(narrow ? 205 : 250);
                SideCol.Width = new GridLength(narrow ? 270 : 330);
                IconCol.Width = new GridLength(narrow ? 104 : 150);
                IconBox.Height = narrow ? 82 : 116;
            };
            _ = LoadAsync();
        }

        public string Title => "Hotkeys";
        public string Crumb => TabKeys.IsChecked == true ? "Game keys" : "Unit and building buttons";
        public string IconKey => "I.keys";
        public FrameworkElement? TopRight => _topRight;
        public FrameworkElement? Footer => _footer;
        // Only the user's own changes stop the window from closing; the team key repair just waits for the next Save
        public bool HasPendingChanges => _hk is { DiscardableCount: > 0 };

        public void OnShown() => UpdateFooter();

        public void FocusSearch()
        {
            if (TabKeys.IsChecked == true)
            {
                KeySearch.Focus();
                KeySearch.SelectAll();
            }
        }

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 60 && _hk == null; i++)
                await Task.Delay(250);
        }

        public void ShowPart(string part)
        {
            if (part == "keys")
                TabKeys.IsChecked = true;
        }

        private async Task LoadAsync()
        {
            _hk = await AppState.Hotkeys();
            if (_hk == null)
            {
                Loading.Text = "Could not read the game's hotkeys. Check that Command Center points at the game folder.";
                return;
            }
            Loading.Visibility = Visibility.Collapsed;
            ButtonsView.Visibility = Visibility.Visible;
            Ui.SetBadge(TabButtons, _hk.ButtonCount.ToString());
            Ui.SetBadge(TabKeys, _hk.ListedGameKeys.Count().ToString());
            FUsa.IsChecked = true;
            FillGameKeys();
            UpdateFooter();
        }

        // ── Army and menus ──

        private void Faction_Checked(object sender, RoutedEventArgs e)
        {
            if (_hk == null) return;
            string faction = FChina.IsChecked == true ? "China" : FGla.IsChecked == true ? "GLA" : "USA";
            _filling = true;
            General.Items.Clear();
            foreach (var army in _hk.Armies.Where(a => a.Faction == faction))
                General.Items.Add(new ComboBoxItem { Content = army.General == null ? $"{faction} (no general)" : army.Name, Tag = army });
            _filling = false;
            General.SelectedIndex = 0;
        }

        private void General_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || General.SelectedItem is not ComboBoxItem { Tag: HotkeyArmy army })
                return;
            _army = army;
            FillMenus();
        }

        private void FillMenus(HotkeyMenu? keep = null)
        {
            if (_hk == null || _army == null) return;
            var items = _army.Menus.Select(m =>
            {
                var worst = m.Buttons.Select(b => _hk.WorstIssue(m, b)).DefaultIfEmpty(IssueLevel.Ok).Max();
                return new MenuItemView
                {
                    Menu = m,
                    Mark = worst switch { IssueLevel.Error => Mark.Danger, IssueLevel.Warn => Mark.Warn, _ => Mark.None },
                    MarkTip = worst switch { IssueLevel.Error => "Two buttons share a key", IssueLevel.Warn => "A key is also a game key", _ => null },
                };
            }).ToList();
            var view = new ListCollectionView(items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MenuItemView.Group)));
            Menus.ItemsSource = view;
            var select = items.FirstOrDefault(i => i.Menu == keep) ?? items.FirstOrDefault(i => i.Name.Contains("War Factory")) ?? items.FirstOrDefault();
            Menus.SelectedItem = select;
            ShowRules();
        }

        private void Menus_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Menus.SelectedItem is not MenuItemView item) return;
            _menu = item.Menu;
            MenuTitle.Text = _menu.Name;
            _button = _menu.Buttons.FirstOrDefault(b => b.Name.Contains("Humvee")) ?? _menu.Buttons.First();
            _listening = false;
            ShowSlots();
            ShowEditor();
        }

        // ── Control bar preview ──

        private void ShowSlots()
        {
            Slots.Children.Clear();
            if (_hk == null || _menu == null) return;
            int count = Math.Max(14, (int)Math.Ceiling(_menu.MaxSlot / 7.0) * 7);
            for (int slot = 1; slot <= count; slot++)
            {
                var button = _menu.Buttons.FirstOrDefault(b => b.Slot == slot);
                var silent = button == null ? _menu.Silent.FirstOrDefault(b => b.Slot == slot) : null;
                Slots.Children.Add(Slot(button, silent));
            }
        }

        private FrameworkElement Slot(HotkeyButton? button, HotkeyButton? silent)
        {
            var grid = new Grid { Width = 70, Height = 56, Margin = new Thickness(3) };
            grid.Children.Add(new Border { Background = (Brush)new BrushConverter().ConvertFrom("#141618")!, BorderBrush = (Brush)new BrushConverter().ConvertFrom("#262B2E")!, BorderThickness = new Thickness(1) });
            var shown = button ?? silent;
            if (shown == null)
                return grid;

            var image = _hk!.Images.Get(shown.Image);
            grid.Children.Add(new Image { Source = image, Stretch = Stretch.UniformToFill, Opacity = button == null ? 0.45 : 1, Margin = new Thickness(1) });
            grid.ToolTip = button == null ? $"{shown.Name} · no hotkey" : $"{button.Name} · {(_hk.KeyFor(button.Label) is var k && k != '\0' ? k.ToString() : "no key")}";
            if (button == null)
                return grid;

            char key = _hk.KeyFor(button.Label);
            var level = _hk.WorstIssue(_menu!, button);
            var keyBox = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFrom("#D9000000")!,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(1),
                Child = new TextBlock { Text = key == '\0' ? "–" : key.ToString(), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            };
            grid.Children.Add(keyBox);

            if (_hk.IsChanged(button.Label))
                grid.Children.Add(new StatusMark { Mark = Mark.Changed, Width = 11, Height = 11, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3) });
            if (level is IssueLevel.Error or IssueLevel.Warn)
                grid.Children.Add(new StatusMark { Mark = level == IssueLevel.Error ? Mark.Danger : Mark.Warn, Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3) });
            if (level == IssueLevel.Error)
                grid.Children.Add(new Rectangle { Stroke = Views.Res("Danger"), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 1.5 } });
            if (button == _button)
                grid.Children.Add(new Border { BorderBrush = Views.Res("Accent"), BorderThickness = new Thickness(2.5), Margin = new Thickness(-2) });

            grid.Cursor = Cursors.Hand;
            grid.MouseLeftButtonUp += (_, _) =>
            {
                _button = button;
                _listening = false;
                ShowSlots();
                ShowEditor();
            };
            return grid;
        }

        // ── Button editor ──

        private void ShowEditor()
        {
            if (_hk == null || _menu == null || _button == null) return;
            var b = _button;
            BigIcon.Source = _hk.Images.Get(b.Image);
            ButtonName.Text = b.Name;
            ButtonWhere.Text = $"Slot {b.Slot} · {_menu.Name}";
            char key = _hk.KeyFor(b.Label), def = _hk.DefaultKeyFor(b.Label);

            KeyCapText.Text = _listening ? "?" : key == '\0' ? "–" : key.ToString();
            if (_listening)
            {
                KeyCap.Background = Views.Res("AccentSoft");
                KeyCap.BorderBrush = Views.Res("AccentHi");
                KeyCapText.Foreground = Views.Res("AccentHi");
                ListenTitle.Text = "Press a new key";
                ListenHint.Text = "A letter A–Z or a number. Button keys never use Ctrl, Alt or Shift; the game ignores them. Esc cancels.";
            }
            else
            {
                KeyCap.Background = Views.Res("GoldFill");
                KeyCap.BorderBrush = (Brush)new BrushConverter().ConvertFrom("#FFDC8F")!;
                KeyCapText.Foreground = (Brush)new BrushConverter().ConvertFrom("#1D1405")!;
                ListenTitle.Text = key == '\0' ? "No key" : $"Key {key}";
                ListenHint.Text = key == def ? (def == '\0' ? "The game gives this button no key; pick one to add it." : "The game's default key.") : $"Changed from the default {(def == '\0' ? "(none)" : def.ToString())}.";
            }
            ResetBtn.Content = def == '\0' ? "Default" : $"Default ({def})";
            ResetBtn.IsEnabled = key != def;
            ClearBtn.IsEnabled = key != '\0';

            Issues.Children.Clear();
            foreach (var issue in _hk.ButtonIssues(_menu, b))
                Issues.Children.Add(IssueRow(issue));

            Suggestions.Children.Clear();
            foreach (char c in _hk.SuggestKeys(_menu, b, 8))
            {
                var chip = new Button { Content = c.ToString(), Style = Views.Style("BtnXs"), Margin = new Thickness(0, 0, 6, 6), MinWidth = 34, ToolTip = $"Use {c}" };
                char pick = c;
                chip.Click += (_, _) => SetButtonKey(pick);
                Suggestions.Children.Add(chip);
            }

            var menus = _hk.MenusUsing(b.Label).ToList();
            SameLabel.Text = menus.Count > 1 ? $"Same button in {menus.Count} menus · the key applies to all" : "Only in this menu";
            SameMenus.Children.Clear();
            foreach (var (army, menu) in menus.Take(6))
                SameMenus.Children.Add(new Border { Style = Views.Style("Tag"), Margin = new Thickness(0, 0, 6, 6), Child = new TextBlock { Text = $"{menu.Name} · {army.Name}", FontSize = 12.5 } });
            if (menus.Count > 6)
                SameMenus.Children.Add(new Border { Style = Views.Style("Tag"), Margin = new Thickness(0, 0, 6, 6), Child = new TextBlock { Text = $"+{menus.Count - 6} more", FontSize = 12.5 } });
        }

        private static FrameworkElement IssueRow(KeyIssue issue)
        {
            var border = new Border
            {
                Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1),
                BorderBrush = issue.Level switch { IssueLevel.Error => Views.Res("DangerLine"), IssueLevel.Warn => Views.Res("WarnLine"), IssueLevel.Info => Views.Res("InfoLine"), _ => Views.Res("Line") },
                Background = issue.Level switch { IssueLevel.Error => Views.Res("DangerSoft"), IssueLevel.Warn => Views.Res("WarnSoft"), IssueLevel.Info => Views.Res("InfoSoft"), _ => Brushes.Transparent },
            };
            var dock = new DockPanel();
            var mark = new StatusMark { Mark = Views.MarkOf(issue.Level), Width = 14, Height = 14, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(mark, Dock.Left);
            dock.Children.Add(mark);
            dock.Children.Add(new TextBlock { Text = issue.Text, Style = Views.Style("Body"), FontSize = 13.5, LineHeight = 19, Foreground = Views.Res("Text") });
            border.Child = dock;
            return border;
        }

        private void SetButtonKey(char key)
        {
            if (_hk == null || _button == null) return;
            _hk.SetKey(_button.Label, key);
            _listening = false;
            Refresh();
        }

        private void Refresh()
        {
            ShowSlots();
            ShowEditor();
            FillMenus(_menu);
            UpdateFooter();
        }

        private void KeyCap_Click(object sender, MouseButtonEventArgs e) => Change_Click(sender, e);

        private void Change_Click(object sender, RoutedEventArgs e)
        {
            _listening = true;
            _keyListening = false;
            ShowEditor();
            Focus();
        }

        private void ResetKey_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null || _button == null) return;
            _hk.ResetKey(_button.Label);
            _listening = false;
            Refresh();
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => SetButtonKey('\0');

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_listening)
            {
                e.Handled = true;
                if (key == Key.Escape)
                {
                    _listening = false;
                    ShowEditor();
                    return;
                }
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
                {
                    ListenHint.Text = "Button keys cannot use Ctrl, Alt or Shift; the game ignores them. Press the letter alone.";
                    return;
                }
                char c = key >= Key.A && key <= Key.Z ? (char)('A' + (key - Key.A))
                       : key >= Key.D0 && key <= Key.D9 ? (char)('0' + (key - Key.D0))
                       : '\0';
                if (c == '\0' || Keyboard.Modifiers != ModifierKeys.None)
                {
                    ListenHint.Text = "Only a letter A–Z or a number, pressed alone, works for buttons.";
                    return;
                }
                SetButtonKey(c);
            }
            else if (_keyListening)
            {
                e.Handled = true;
                if (key == Key.Escape)
                {
                    _keyListening = false;
                    ShowGameKey();
                    return;
                }
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
                    return;
                string? name = HotkeyService.GameKeyName(key);
                if (name == null || _gameKey == null || _hk == null)
                {
                    KHint.Text = "The game cannot use that key. Try a letter, number, F-key or numpad key.";
                    return;
                }
                if (HotkeyService.CannotUse(_gameKey, name) is { } reason)
                {
                    KHint.Text = reason;
                    return;
                }
                var mods = Keyboard.Modifiers;
                _hk.SetGameKey(_gameKey, name,HotkeyService.ModifiersName(mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Shift)));
                _keyListening = false;
                FillGameKeys(_gameKey);
                UpdateFooter();
            }
        }

        // ── Rules and probe ──

        private void ShowRules()
        {
            Rules.Children.Clear();
            if (_hk == null || _army == null) return;
            var dupMenus = _army.Menus.Where(m => m.Buttons.Any(b => _hk.WorstIssue(m, b) == IssueLevel.Error)).ToList();
            var shared = _army.Menus.SelectMany(m => m.Buttons).Where(b => _hk.SharedGameKey(b) != null)
                .GroupBy(b => _hk.KeyFor(b.Label)).ToList();
            var badGameKeys = _hk.ListedGameKeys.Where(k => _hk.GameKeyIssues(k).Any(i => i.Level == IssueLevel.Error)).ToList();

            Rules.Children.Add(RuleRow(dupMenus.Count == 0 ? Mark.Ok : Mark.Danger,
                dupMenus.Count == 0 ? "No key twice inside a menu" : $"{dupMenus.Count} {(dupMenus.Count == 1 ? "menu has" : "menus have")} a key twice",
                dupMenus.Count == 0 ? "A repeated key makes the second button dead" : string.Join(", ", dupMenus.Take(3).Select(m => m.Name))));
            Rules.Children.Add(RuleRow(shared.Count == 0 ? Mark.Ok : Mark.Warn,
                shared.Count == 0 ? "No button key is also a game key" : $"{shared.Count} {(shared.Count == 1 ? "key is" : "keys are")} also game keys",
                shared.Count == 0 ? "Pressing a key does one thing" : string.Join(" · ", shared.Take(3).Select(g => $"{g.Key} = {_hk.PlainGlobalKey(g.Key)!.Name}"))));
            Rules.Children.Add(RuleRow(badGameKeys.Count == 0 ? Mark.Ok : Mark.Danger,
                badGameKeys.Count == 0 ? "Game keys are unique" : $"{badGameKeys.Count} game keys clash",
                badGameKeys.Count == 0 ? $"{_hk.ListedGameKeys.Count()} commands, no shared combination" : string.Join(", ", badGameKeys.Take(3).Select(k => k.Name))));
            int teams = _hk.ChangedTeamKeys;
            Rules.Children.Add(RuleRow(teams == 0 ? Mark.Ok : Mark.Warn,
                teams == 0 ? "Team keys on the game's defaults" : $"{teams} team {(teams == 1 ? "key was" : "keys were")} changed earlier",
                teams == 0 ? "0–9 alone and with Ctrl, Shift or Alt stay for teams" : "Team keys always use the game's defaults; Save puts them back"));
        }

        private static FrameworkElement RuleRow(Mark mark, string title, string detail)
        {
            var dock = new DockPanel { Margin = new Thickness(20, 12, 20, 4) };
            var m = new StatusMark { Mark = mark, Width = 15, Height = 15, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(m, Dock.Left);
            dock.Children.Add(m);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = detail, Style = Views.Style("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            dock.Children.Add(text);
            return dock;
        }

        private void Probe_Click(object sender, MouseButtonEventArgs e) => Probe.Focus();

        private void Probe_Focus(object sender, KeyboardFocusChangedEventArgs e)
        {
            bool focused = Probe.IsKeyboardFocused;
            Probe.BorderBrush = focused ? Views.Res("Accent") : Views.Res("InputLine");
            if (focused)
                ProbeText.Text = "Listening… press a key";
        }

        private void Probe_KeyDown(object sender, KeyEventArgs e)
        {
            if (_hk == null || _army == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
                return;
            e.Handled = true;
            string? name = HotkeyService.GameKeyName(key);
            var mods = Keyboard.Modifiers;
            string modName = HotkeyService.ModifiersName(mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Shift));
            ProbeResult.Children.Clear();
            if (name == null)
            {
                ProbeText.Text = "The game does not use that key";
                return;
            }
            ProbeText.Text = HotkeyService.Describe(name, modName);
            var users = new List<string>();
            foreach (var gk in _hk.GameKeys.Where(k => k.Key == name && k.Modifiers == modName))
                users.Add($"Game key: {gk.Name}");
            if (modName == "NONE" && name.Length == 5)
                users.AddRange(_hk.WhoUses(_army, name[4]).Where(u => !u.StartsWith("Game key")));
            if (users.Count == 0)
                ProbeResult.Children.Add(new TextBlock { Text = $"Free in {_army.Name}.", Style = Views.Style("Body"), FontSize = 13.5 });
            foreach (string user in users.Take(12))
                ProbeResult.Children.Add(new TextBlock { Text = "• " + user, Style = Views.Style("Body"), FontSize = 13.5, LineHeight = 20 });
            if (users.Count > 12)
                ProbeResult.Children.Add(new TextBlock { Text = $"and {users.Count - 12} more", Style = Views.Style("Small") });
        }

        // ── Layouts ──

        private void Classic_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null) return;
            _hk.ApplyClassicLayout();
            _hk.ResetGameKeys();
            Refresh();
            FillGameKeys(_gameKey);
            Views.Main.Toast("Game defaults loaded. Press Save to keep them.");
        }

        private void Grid_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null) return;
            _hk.ApplyGridLayout();
            Refresh();
            Views.Main.Toast("Grid layout loaded. Check the rules, then press Save.");
        }

        // ── Game keys ──

        private static readonly string[] CategoryOrder = { "TEAM", "SELECTION", "CONTROL", "INTERFACE", "CAMERA", "CHAT", "BUILT-IN" };

        private static string GroupName(GameKey k)
        {
            string team = HotkeyService.TeamGroupOf(k);
            if (team.Length > 0)
                return team switch { "SELECT" => "Select a team", "CREATE" => "Make a team", "ADD" => "Add to a team", _ => "Jump to a team" };
            return k.Category switch
            {
                "SELECTION" => "Selection", "CONTROL" => "Unit orders", "INTERFACE" => "Interface", "CAMERA" => "Camera",
                "CHAT" => "Chat and beacons", "BUILT-IN" => "Built into the game", _ => k.Category,
            };
        }

        private void FillGameKeys(GameKey? keep = null)
        {
            if (_hk == null) return;
            var rows = _hk.ListedGameKeys
                .OrderBy(k => Array.IndexOf(CategoryOrder, k.Category) is int i && i >= 0 ? i : 50)
                .ThenBy(k => k.Order)
                .Select(k =>
                {
                    var issues = _hk.GameKeyIssues(k);
                    var worst = issues.Select(i => i.Level).DefaultIfEmpty(IssueLevel.Ok).Max();
                    return new GameKeyView
                    {
                        Key = k, Group = GroupName(k),
                        Mark = worst == IssueLevel.Error ? Mark.Danger : worst == IssueLevel.Warn ? Mark.Warn : k.Changed ? Mark.Changed : Mark.None,
                        MarkTip = issues.FirstOrDefault()?.Text ?? (k.Changed ? "Changed from default" : null),
                    };
                }).ToList();
            _keyView = new ListCollectionView(rows);
            _keyView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GameKeyView.Group)));
            _keyView.Filter = o => o is GameKeyView v && (KeySearch.Text.Length == 0 || v.Name.Contains(KeySearch.Text, StringComparison.OrdinalIgnoreCase) || v.Shortcut.Contains(KeySearch.Text, StringComparison.OrdinalIgnoreCase));
            KeyList.ItemsSource = _keyView;
            KeyList.SelectedItem = rows.FirstOrDefault(r => r.Key == keep) ?? rows.FirstOrDefault();
            ShowRules();
        }

        private void KeySearch_Changed(object sender, TextChangedEventArgs e) => _keyView?.Refresh();

        private void KeyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KeyList.SelectedItem is GameKeyView v)
            {
                _gameKey = v.Key;
                _keyListening = false;
                ShowGameKey();
            }
        }

        private void ShowGameKey()
        {
            if (_hk == null || _gameKey == null) return;
            var k = _gameKey;
            KCategory.Text = GroupName(k);
            KName.Text = k.Name;
            KChange.IsEnabled = k.CanChange;
            KReset.Content = "Default (" + k.DefaultShortcut + ")";
            KReset.IsEnabled = k.CanChange && k.Changed;
            if (_keyListening)
            {
                KCapText.Text = "Press the new keys…";
                KCap.Background = Views.Res("AccentSoft");
                KCap.BorderBrush = Views.Res("AccentHi");
                KCapText.Foreground = Views.Res("AccentHi");
                KHint.Text = "Hold Ctrl, Alt or Shift if you want them, then press the key. Esc cancels.";
            }
            else
            {
                KCapText.Text = k.Shortcut;
                KCap.Background = (Brush)new BrushConverter().ConvertFrom("#1E2225")!;
                KCap.BorderBrush = k.Pending ? Views.Res("Info") : Views.Res("LineHi");
                KCapText.Foreground = Views.Res("Text");
                KHint.Text = k.IsFixed ? "The game adds this key by itself and cannot change it. It is listed so you do not pick it for something else."
                    : k.Changed ? $"Changed from {k.DefaultShortcut}." + (k.IsBuiltIn ? " Only Generals Online reads this change." : "")
                    : k.IsBuiltIn ? "The game adds this key by itself." : "The game's default.";
            }
            KIssues.Children.Clear();
            var issues = _hk.GameKeyIssues(k);
            if (issues.Count == 0 && k.CanChange)
                issues.Add(new KeyIssue(IssueLevel.Ok, "No other command uses this combination."));
            foreach (var issue in issues)
                KIssues.Children.Add(IssueRow(issue));
        }

        private void KCap_Click(object sender, MouseButtonEventArgs e) => KChange_Click(sender, e);

        private void KChange_Click(object sender, RoutedEventArgs e)
        {
            if (_gameKey == null || !_gameKey.CanChange) return;
            _keyListening = true;
            _listening = false;
            ShowGameKey();
            Focus();
        }

        private void KReset_Click(object sender, RoutedEventArgs e)
        {
            if (_hk == null || _gameKey == null) return;
            _hk.ResetGameKey(_gameKey);
            FillGameKeys(_gameKey);
            UpdateFooter();
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (ButtonsView == null || _hk == null) return;
            bool keys = TabKeys.IsChecked == true;
            ButtonsView.Visibility = keys ? Visibility.Collapsed : Visibility.Visible;
            KeysView.Visibility = keys ? Visibility.Visible : Visibility.Collapsed;
            ArmyBar.Visibility = keys ? Visibility.Collapsed : Visibility.Visible;
            Views.Main.RefreshHeader();
        }

        // ── Save bar (only while there are changes) ──

        private void BuildFooter()
        {
            var save = new Button { Content = "Save hotkeys", Style = Views.Style("BtnLg") };
            Ui.SetVariant(save, Variant.Primary);
            Ui.SetIcon(save, Views.Icon("check"));
            save.Click += (_, _) => Save();
            DockPanel.SetDock(save, Dock.Right);
            var discard = new Button { Content = "Discard", Style = Views.Style("BtnLg"), Margin = new Thickness(0, 0, 12, 0) };
            Ui.SetVariant(discard, Variant.Ghost);
            discard.Click += (_, _) =>
            {
                _hk?.DiscardPending();
                Refresh();
                FillGameKeys(_gameKey);
                UpdateFooter();
            };
            DockPanel.SetDock(discard, Dock.Right);
            _footer.Children.Add(save);
            _footer.Children.Add(discard);
            var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new StatusMark { Mark = Mark.Changed, Width = 14, Height = 14, Margin = new Thickness(0, 0, 10, 0) });
            text.Children.Add(_pendingText);
            text.Children.Add(new TextBlock { Text = "   ·   ", Foreground = Views.Res("Text3"), VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(_whereText);
            _footer.Children.Add(text);
            _footer.Visibility = Visibility.Collapsed;
        }

        private void UpdateFooter()
        {
            if (_hk == null) return;
            int count = _hk.PendingCount;
            _footer.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _pendingText.Text = count == 1 ? "1 change not saved" : $"{count} changes not saved";
            int teams = _hk.ChangedTeamKeys, discardable = _hk.DiscardableCount;
            _whereText.Text = (teams > 0 ? $"{teams} team {(teams == 1 ? "key goes" : "keys go")} back to the game's defaults. " : "")
                + $"Will be saved to {string.Join(" and ", _hk.PendingFiles())}. A backup is kept.";
            if (_footer.Children.Count > 1 && _footer.Children[1] is Button discard)
            {
                discard.Content = discardable == 1 ? "Discard 1 change" : $"Discard {discardable} changes";
                discard.Visibility = discardable > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            Views.Main.RefreshFooter();
        }

        private void Save()
        {
            if (_hk == null) return;
            if (GameLauncher.IsGameRunning())
            {
                Views.Main.Toast("Close the game first; it reads hotkeys only when it starts.", isError: true);
                return;
            }
            int before = BackupService.Load().Count;
            try
            {
                string files = _hk.Save();
                var made = BackupService.Load().Skip(before).ToList();
                Views.Main.Toast($"Saved to {files}. They apply the next time the game starts.", () =>
                {
                    foreach (var entry in Enumerable.Reverse(made))
                        BackupService.Undo(entry);
                    _hk.Reload();
                    Refresh();
                    FillGameKeys(_gameKey);
                    Views.Main.Toast("Hotkeys put back");
                });
            }
            catch (Exception ex)
            {
                Views.Main.Toast("Could not save: " + ex.Message, isError: true);
            }
            Refresh();
            FillGameKeys(_gameKey);
        }
    }
}
