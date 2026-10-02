using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public enum Store { Options, Go, Launcher }

    public enum SettingKind { Toggle, Number, Choice }

    // One option. Values are kept as text: "true"/"false" for toggles, digits for numbers, the raw value for choices.
    public sealed class Setting
    {
        public required string Section { get; init; }
        public required string Label { get; init; }
        public string Help { get; init; } = "";
        public required Store Store { get; init; }
        public required string Key { get; init; }           // Options.ini key, "section.key" in settings.json, or a launcher.json key
        public required SettingKind Kind { get; init; }
        public required string Default { get; init; }
        public List<(string Value, string Label)> Choices { get; set; } = new();
        public double Min { get; init; }
        public double Max { get; init; }
        public double Step { get; init; } = 1;
        public string Unit { get; init; } = "";
        public string YesWord { get; init; } = "yes";        // how Options.ini spells a toggle
        public string NoWord { get; init; } = "no";
        public bool IsDecimal { get; init; }
        public string? EnabledBy { get; init; }              // key of a toggle that has to be on

        public string Show(string value) => Kind switch
        {
            SettingKind.Toggle => value == "true" ? "On" : "Off",
            SettingKind.Choice => Choices.FirstOrDefault(c => c.Value == value).Label ?? value,
            _ => value + Unit,
        };
    }

    public partial class SettingsPage : UserControl, IPage
    {
        private readonly List<Setting> _settings;
        private readonly Dictionary<Setting, string> _saved = new();
        private readonly Dictionary<Setting, string> _pending = new();
        private string _section = "Display";
        private readonly DockPanel _footer = new();
        private readonly TextBlock _pendingText = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _whereText = new() { FontSize = 13, Foreground = Views.Res("Text2"), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _discard = new();

        private static readonly (string Name, string Icon, string Subtitle)[] SectionInfo =
        {
            ("Display", "monitor", "Window, resolution and frame rate"),
            ("Camera and mouse", "camera", "Zoom limits, scrolling and clicks"),
            ("Chat and interface", "chat", "Messages and what the screen shows"),
            ("Notifications", "users", "Friends coming and going"),
            ("Network and data", "globe", "Generals Online connection and data"),
        };

        public SettingsPage()
        {
            InitializeComponent();
            _settings = Define();
            Load();
            BuildSections();
            BuildFooter();
            ShowSection(_section);
        }

        public string Title => "Settings";
        public string Crumb => _section;
        public string IconKey => "I.gear";
        public FrameworkElement? TopRight => null;
        public FrameworkElement? Footer => _footer;
        public bool HasPendingChanges => _pending.Count > 0;

        public void OnShown()
        {
            if (_pending.Count == 0)
            {
                Load();
                ShowSection(_section);
            }
            UpdateFooter();
        }

        public void ShowPart(string part)
        {
            var match = SectionInfo.FirstOrDefault(s => s.Name.StartsWith(part, StringComparison.OrdinalIgnoreCase));
            if (match.Name != null)
                ShowSection(match.Name);
        }

        // ── What can be changed ──
        // Only options the game's own menu cannot reach, or that live in Generals Online's and the launcher's files.

        private static List<Setting> Define()
        {
            var display = HealthService.Display.Current();
            var modes = HealthService.Display.Modes().Where(m => m.Width >= 800).ToList();
            var windowSizes = new[] { (1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440) }.Where(s => s.Item1 <= Math.Max(display.Width, 1280)).ToList();

            var list = new List<Setting>
            {
                new() { Section = "Display", Label = "Play in a window", Help = "Starts the game in a window instead of full screen.", Store = Store.Launcher, Key = "windowed", Kind = SettingKind.Toggle, Default = "false" },
                new() { Section = "Display", Label = "Window size", Help = "Used when playing in a window.", Store = Store.Launcher, Key = "windowed_size", Kind = SettingKind.Choice, Default = "1920x1080", EnabledBy = "windowed",
                        Choices = windowSizes.Select(s => ($"{s.Item1}x{s.Item2}", $"{s.Item1} × {s.Item2}")).ToList() },
                new() { Section = "Display", Label = "Full-screen resolution", Help = "The game's own setting in Options.ini.", Store = Store.Options, Key = "Resolution", Kind = SettingKind.Choice, Default = $"{display.Width} {display.Height}",
                        Choices = modes.Select(m => ($"{m.Width} {m.Height}", $"{m.Width} × {m.Height}")).ToList() },
                new() { Section = "Display", Label = "Limit frame rate", Help = "Keeps the game smooth and cool. Online games run at the same speed either way.", Store = Store.Go, Key = "render.limit_framerate", Kind = SettingKind.Toggle, Default = "true" },
                new() { Section = "Display", Label = "Frame limit", Store = Store.Go, Key = "render.fps_limit", Kind = SettingKind.Number, Default = "60", Min = 30, Max = 360, Step = 10, Unit = " fps", EnabledBy = "render.limit_framerate" },
                new() { Section = "Display", Label = "Show FPS and ping", Help = "Generals Online's small overlay in the corner.", Store = Store.Go, Key = "render.stats_overlay", Kind = SettingKind.Toggle, Default = "true" },

                new() { Section = "Camera and mouse", Label = "Closest zoom", Help = "Lower numbers let you zoom in closer.", Store = Store.Go, Key = "camera.min_height", Kind = SettingKind.Number, Default = "210", Min = 100, Max = 310, Step = 10 },
                new() { Section = "Camera and mouse", Label = "Farthest zoom when you host", Help = "Applies to every player in a lobby you host.", Store = Store.Go, Key = "camera.max_height_only_when_lobby_host", Kind = SettingKind.Number, Default = "310", Min = 210, Max = 600, Step = 10 },
                new() { Section = "Camera and mouse", Label = "Camera speed", Store = Store.Go, Key = "camera.move_speed_ratio", Kind = SettingKind.Number, Default = "1", Min = 0.5, Max = 2, Step = 0.1, Unit = "×", IsDecimal = true },
                new() { Section = "Camera and mouse", Label = "Scroll speed", Store = Store.Options, Key = "ScrollFactor", Kind = SettingKind.Number, Default = "50", Min = 0, Max = 100, Step = 5 },
                new() { Section = "Camera and mouse", Label = "Scroll at screen edge in full screen", Store = Store.Options, Key = "ScreenEdgeScrollEnabledInFullscreenApp", Kind = SettingKind.Toggle, Default = "true" },
                new() { Section = "Camera and mouse", Label = "Scroll at screen edge in a window", Help = "Handy when the mouse is locked to the window.", Store = Store.Options, Key = "ScreenEdgeScrollEnabledInWindowedApp", Kind = SettingKind.Toggle, Default = "false" },
                new() { Section = "Camera and mouse", Label = "Keep the mouse inside the window", Store = Store.Options, Key = "CursorCaptureEnabledInWindowedGame", Kind = SettingKind.Toggle, Default = "true" },
                new() { Section = "Camera and mouse", Label = "Left-click to give orders", Help = "The game's alternate mouse setup.", Store = Store.Options, Key = "UseAlternateMouse", Kind = SettingKind.Toggle, Default = "false" },
                new() { Section = "Camera and mouse", Label = "Double-click for attack move", Store = Store.Options, Key = "UseDoubleClickAttackMove", Kind = SettingKind.Toggle, Default = "false" },

                new() { Section = "Chat and interface", Label = "Chat fades after", Store = Store.Go, Key = "chat.duration_seconds_until_fade_out", Kind = SettingKind.Number, Default = "30", Min = 5, Max = 120, Step = 5, Unit = " s" },
                new() { Section = "Chat and interface", Label = "Hide bad words in chat", Store = Store.Options, Key = "LanguageFilter", Kind = SettingKind.Toggle, Default = "true", YesWord = "true", NoWord = "false" },
                new() { Section = "Chat and interface", Label = "Show money per minute", Store = Store.Options, Key = "ShowMoneyPerMinute", Kind = SettingKind.Toggle, Default = "false" },
                new() { Section = "Chat and interface", Label = "Units fight back when attacked", Help = "The game's retaliation option.", Store = Store.Options, Key = "Retaliation", Kind = SettingKind.Toggle, Default = "true" },

                new() { Section = "Network and data", Label = "Community data patch", Help = "Generals Online's balance and bug fixes. Online games need it; turn it off only if support asks.", Store = Store.Go, Key = "data_packs.use_community_data_patch", Kind = SettingKind.Toggle, Default = "true" },
                new() { Section = "Network and data", Label = "Use the alternative server address", Help = "Try this if your network blocks the main address.", Store = Store.Go, Key = "network.use_alternative_endpoint", Kind = SettingKind.Toggle, Default = "false" },
                new() { Section = "Network and data", Label = "Connection type", Store = Store.Go, Key = "network.http_version", Kind = SettingKind.Choice, Default = "0",
                        Choices = new() { ("0", "Automatic"), ("1", "HTTP/1.1"), ("2", "HTTP/2"), ("3", "HTTP/3") } },
                new() { Section = "Network and data", Label = "Anti-cheat", Help = "Which anti-cheat Generals Online uses.", Store = Store.Go, Key = "plugins.anticheat", Kind = SettingKind.Choice, Default = "easyanticheat", Choices = AntiCheats() },
            };

            foreach (var (key, label) in new[]
            {
                ("friend_comes_online", "A friend comes online"), ("friend_goes_offline", "A friend goes offline"),
                ("player_sends_request", "Someone sends you a friend request"), ("player_accepts_request", "Someone accepts your request"),
            })
            {
                list.Add(new() { Section = "Notifications", Label = label, Help = "In menus", Store = Store.Go, Key = $"social.notification_{key}_menus", Kind = SettingKind.Toggle, Default = "true" });
                list.Add(new() { Section = "Notifications", Label = label, Help = "During a match", Store = Store.Go, Key = $"social.notification_{key}_gameplay", Kind = SettingKind.Toggle, Default = "true" });
            }
            return list;
        }

        private static List<(string, string)> AntiCheats()
        {
            var list = new List<(string, string)>();
            string folder = Path.Combine(GamePaths.Game, "plugins");
            if (!Directory.Exists(folder))
                return list;
            foreach (string dir in Directory.EnumerateDirectories(folder).OrderBy(d => d))
            {
                string id = Path.GetFileName(dir);
                string label = id;
                try
                {
                    string? json = Directory.EnumerateFiles(dir, "*.json").FirstOrDefault();
                    if (json != null)
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(json));
                        if (doc.RootElement.TryGetProperty("plugin_name", out var name))
                            label = name.GetString() ?? id;
                    }
                }
                catch { }
                list.Add((id, label));
            }
            return list;
        }

        // ── Reading and writing ──

        private void Load()
        {
            var options = OptionsFile.Read();
            var go = GoSettings.Load();
            var launcher = LauncherJson.Load();
            _saved.Clear();
            foreach (var s in _settings)
            {
                string? value = s.Store switch
                {
                    Store.Options => options.TryGetValue(s.Key, out var v) ? (s.Kind == SettingKind.Toggle ? (v.Equals(s.YesWord, StringComparison.OrdinalIgnoreCase) ? "true" : "false") : v) : null,
                    Store.Go => go.Root[s.Key.Split('.')[0]]?[s.Key.Split('.')[1]]?.ToString(),
                    _ => s.Key == "windowed_size" ? $"{launcher.WindowedWidth}x{launcher.WindowedHeight}" : launcher.Root[s.Key]?.ToString(),
                };
                if (value != null && s.Kind == SettingKind.Toggle)
                    value = value.ToLowerInvariant();
                if (value != null && s.Kind == SettingKind.Number && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    value = d.ToString(CultureInfo.InvariantCulture);
                _saved[s] = value ?? s.Default;
            }
        }

        private string Value(Setting s) => _pending.TryGetValue(s, out var v) ? v : _saved[s];

        private void Set(Setting s, string value)
        {
            if (value == _saved[s]) _pending.Remove(s);
            else _pending[s] = value;
            ShowSection(_section, keepScroll: true);
            UpdateFooter();
        }

        private void Save()
        {
            if (_pending.Count == 0)
                return;
            int before = BackupService.Load().Count;
            try
            {
                var options = _pending.Where(p => p.Key.Store == Store.Options).ToList();
                if (options.Count > 0)
                    OptionsFile.Write(options.ToDictionary(p => p.Key.Key, p => p.Key.Kind == SettingKind.Toggle ? (p.Value == "true" ? p.Key.YesWord : p.Key.NoWord) : p.Value),
                        "Saved settings", "Options.ini · " + string.Join(", ", options.Select(p => p.Key.Label)));

                var go = _pending.Where(p => p.Key.Store == Store.Go).ToList();
                if (go.Count > 0)
                {
                    var settings = GoSettings.Load();
                    foreach (var (s, value) in go)
                    {
                        string[] parts = s.Key.Split('.');
                        if (s.Kind == SettingKind.Toggle) settings.Set(parts[0], parts[1], value == "true");
                        else if (s.Kind == SettingKind.Number && s.IsDecimal) settings.Set(parts[0], parts[1], double.Parse(value, CultureInfo.InvariantCulture));
                        else if (s.Kind == SettingKind.Number || (s.Kind == SettingKind.Choice && int.TryParse(value, out _))) settings.Set(parts[0], parts[1], int.Parse(value, CultureInfo.InvariantCulture));
                        else settings.Set(parts[0], parts[1], value);
                    }
                    settings.Save("settings.json · " + string.Join(", ", go.Select(p => p.Key.Label)));
                }

                var launcherChanges = _pending.Where(p => p.Key.Store == Store.Launcher).ToList();
                if (launcherChanges.Count > 0)
                {
                    var launcher = LauncherJson.Load();
                    foreach (var (s, value) in launcherChanges)
                    {
                        if (s.Key == "windowed_size")
                        {
                            var wh = value.Split('x');
                            launcher.WindowedWidth = int.Parse(wh[0]);
                            launcher.WindowedHeight = int.Parse(wh[1]);
                        }
                        else if (s.Key == "windowed")
                            launcher.Windowed = value == "true";
                    }
                    launcher.SaveWithBackup("launcher.json · " + string.Join(", ", launcherChanges.Select(p => p.Key.Label)));
                }

                var made = BackupService.Load().Skip(before).ToList();
                int count = _pending.Count;
                _pending.Clear();
                Load();
                ShowSection(_section);
                UpdateFooter();
                Views.Main.Toast($"Saved {count} {(count == 1 ? "setting" : "settings")}. They apply the next time the game starts.", () =>
                {
                    foreach (var entry in Enumerable.Reverse(made))
                        BackupService.Undo(entry);
                    Load();
                    ShowSection(_section);
                    Views.Main.Toast("Settings put back");
                });
            }
            catch (Exception ex)
            {
                Views.Main.Toast("Could not save: " + ex.Message, isError: true);
            }
        }

        // ── Layout ──

        private void BuildSections()
        {
            Sections.Children.Clear();
            foreach (var (name, _, _) in SectionInfo)
            {
                var tab = new RadioButton { Content = name, Style = Views.Style("SectionTab"), GroupName = "settings", Tag = name, IsChecked = name == _section };
                tab.Checked += (_, _) => ShowSection(name);
                Sections.Children.Add(tab);
            }
        }

        private void UpdateSectionBadges()
        {
            foreach (RadioButton tab in Sections.Children)
            {
                int n = _pending.Keys.Count(s => s.Section == (string)tab.Tag);
                Ui.SetBadge(tab, n > 0 ? $"{n} unsaved" : null);
            }
        }

        private void ShowSection(string name, bool keepScroll = false)
        {
            _section = name;
            var info = SectionInfo.First(s => s.Name == name);
            SectionHead.Header = name;
            Ui.SetSubtitle(SectionHead, info.Subtitle);
            Ui.SetIcon(SectionHead, Views.Icon(info.Icon));
            foreach (RadioButton tab in Sections.Children)
                if ((string)tab.Tag == name && tab.IsChecked != true)
                    tab.IsChecked = true;

            Rows.Children.Clear();
            var settings = _settings.Where(s => s.Section == name).ToList();
            ResetSection.IsEnabled = settings.Any(s => Value(s) != s.Default);
            foreach (var s in settings)
                Rows.Children.Add(Row(s));
            UpdateSectionBadges();
            if (IsLoaded)
                Views.Main.RefreshHeader();
        }

        private FrameworkElement Row(Setting s)
        {
            string value = Value(s);
            bool enabled = s.EnabledBy == null || Value(_settings.First(o => o.Key == s.EnabledBy)) == "true";
            var grid = new Grid { Margin = new Thickness(22, 0, 18, 0), MinHeight = 64, IsEnabled = enabled, Opacity = enabled ? 1 : 0.5 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

            // Label, help, and a blue diamond while the value differs from the saved one
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 16, 10) };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            if (_pending.ContainsKey(s))
                title.Children.Add(new StatusMark { Mark = Mark.Changed, Width = 11, Height = 11, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Not saved yet" });
            title.Children.Add(new TextBlock { Text = s.Label, FontSize = 15, FontWeight = FontWeights.SemiBold });
            text.Children.Add(title);
            if (s.Help.Length > 0)
                text.Children.Add(new TextBlock { Text = s.Help, Style = Views.Style("Small"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 3, 0, 0) });
            grid.Children.Add(text);

            FrameworkElement control = s.Kind switch
            {
                SettingKind.Toggle => Toggle(s, value),
                SettingKind.Number => Number(s, value),
                _ => Choice(s, value),
            };
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);

            // Default and a reset for this one option
            var reset = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            var undo = new Button { Style = Views.Style("IconBtn"), Width = 30, Height = 30, IsEnabled = value != s.Default, ToolTip = $"Back to the default ({s.Show(s.Default)})" };
            Ui.SetIcon(undo, Views.Icon("undo"));
            undo.Click += (_, _) => Set(s, s.Default);
            undo.Opacity = undo.IsEnabled ? 1 : 0.35;
            DockPanel.SetDock(undo, Dock.Right);
            reset.Children.Add(undo);
            reset.Children.Add(new TextBlock { Text = "Default " + s.Show(s.Default), Style = Views.Style("Small"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right });
            Grid.SetColumn(reset, 2);
            grid.Children.Add(reset);

            return new Border { Child = grid, BorderBrush = Views.Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
        }

        private FrameworkElement Toggle(Setting s, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var box = new CheckBox { Style = Views.Style("Switch"), IsChecked = value == "true" };
            System.Windows.Automation.AutomationProperties.SetName(box, s.Label);
            box.Click += (_, _) => Set(s, box.IsChecked == true ? "true" : "false");
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = value == "true" ? "On" : "Off", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Views.Res("Text2") });
            return row;
        }

        private FrameworkElement Number(Setting s, string value)
        {
            double v = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : double.Parse(s.Default, CultureInfo.InvariantCulture);
            var dock = new DockPanel();
            var label = new TextBlock { Width = 70, TextAlignment = TextAlignment.Right, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            string Format(double x) => s.IsDecimal ? x.ToString("0.0", CultureInfo.InvariantCulture) : x.ToString("0", CultureInfo.InvariantCulture);
            label.Text = Format(v) + s.Unit;
            DockPanel.SetDock(label, Dock.Right);
            dock.Children.Add(label);
            var slider = new Slider { Minimum = s.Min, Maximum = s.Max, Value = v, SmallChange = s.Step, LargeChange = s.Step * 2, TickFrequency = s.Step, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 12, 0) };
            System.Windows.Automation.AutomationProperties.SetName(slider, s.Label);
            slider.ValueChanged += (_, e) => label.Text = Format(e.NewValue) + s.Unit;
            // Commit when the drag or key press ends, so the page does not rebuild on every tick
            slider.PreviewMouseUp += (_, _) => Commit();
            slider.KeyUp += (_, _) => Commit();
            void Commit()
            {
                string text = s.IsDecimal ? Math.Round(slider.Value, 1).ToString(CultureInfo.InvariantCulture) : Math.Round(slider.Value).ToString(CultureInfo.InvariantCulture);
                if (text != Value(s))
                    Set(s, text);
            }
            dock.Children.Add(slider);
            return dock;
        }

        private FrameworkElement Choice(Setting s, string value)
        {
            var combo = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetName(combo, s.Label);
            var choices = s.Choices.ToList();
            if (!choices.Any(c => c.Value == value))
                choices.Insert(0, (value, s.Show(value) + " (current)"));
            foreach (var (v, label) in choices)
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = v });
            combo.SelectedIndex = choices.FindIndex(c => c.Value == value);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: string v })
                    Set(s, v);
            };
            return combo;
        }

        private void ResetSection_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _settings.Where(s => s.Section == _section))
            {
                if (s.Default == _saved[s]) _pending.Remove(s);
                else _pending[s] = s.Default;
            }
            ShowSection(_section);
            UpdateFooter();
        }

        // ── Save bar (only while there are changes) ──

        private void BuildFooter()
        {
            var save = new Button { Content = "Save settings", Style = Views.Style("BtnLg") };
            Ui.SetVariant(save, Variant.Primary);
            Ui.SetIcon(save, Views.Icon("check"));
            save.Click += (_, _) => Save();
            DockPanel.SetDock(save, Dock.Right);
            _discard.Style = Views.Style("BtnLg");
            _discard.Margin = new Thickness(0, 0, 12, 0);
            Ui.SetVariant(_discard, Variant.Ghost);
            _discard.Click += (_, _) =>
            {
                _pending.Clear();
                ShowSection(_section);
                UpdateFooter();
            };
            DockPanel.SetDock(_discard, Dock.Right);
            _footer.Children.Add(save);
            _footer.Children.Add(_discard);
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
            int n = _pending.Count;
            _footer.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            _pendingText.Text = n == 1 ? "1 change not saved" : $"{n} changes not saved";
            var files = _pending.Keys.Select(s => s.Store switch { Store.Options => "Options.ini", Store.Go => "settings.json", _ => "launcher.json" }).Distinct().ToList();
            _whereText.Text = files.Count == 0 ? "" : $"Will be saved to {string.Join(" and ", files)}. A backup is kept.";
            _discard.Content = n == 1 ? "Discard 1 change" : $"Discard {n} changes";
            if (IsLoaded)
                Views.Main.RefreshFooter();
        }
    }
}
