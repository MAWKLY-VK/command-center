using CommandCenter.Models;
using CommandCenter.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace CommandCenter.Pages
{
    public partial class OptionsPage : Page
    {
        // What each control showed after loading. Only controls the player changed are written back, so values this page
        // cannot show exactly (decimals, separate game and menu flags) and fields it does not know stay as they are.
        private Dictionary<string, string> _shown = new();
        private bool _loaded;
        private string _anticheat = "";
        private bool _anticheatTouched;
        private bool _populatingPlugins;
        private NetworkDiagnosticsResult? _lastDiagResult;
        private CancellationTokenSource? _diagCts;

        // Diagnostic colour palette
        private static SolidColorBrush DiagGreen  => new(Color.FromRgb(0x55, 0xCC, 0x55));
        private static SolidColorBrush DiagYellow => new(Color.FromRgb(0xFF, 0xAA, 0x00));
        private static SolidColorBrush DiagRed    => new(Color.FromRgb(0xFF, 0x44, 0x44));
        private static SolidColorBrush DiagGray   => new(Color.FromRgb(0x55, 0x55, 0x88));

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi, Size = 220)]
        private struct DEVMODE
        {
            [FieldOffset(36)] public ushort dmSize;
            [FieldOffset(108)] public uint dmPelsWidth;
            [FieldOffset(112)] public uint dmPelsHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        private List<(int Width, int Height)> GetSupportedResolutions()
        {
            var seen = new HashSet<(int, int)>();
            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf(dm);
            int i = 0;
            while (EnumDisplaySettings(null, i++, ref dm))
                seen.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight));

            return seen
                .Where(r => r.Item1 >= 640 && r.Item2 >= 480)
                .OrderByDescending(r => r.Item1).ThenByDescending(r => r.Item2)
                .ToList();
        }

        private void PopulateResolutions(int selectedWidth, int selectedHeight)
        {
            var resolutions = GetSupportedResolutions();
            cmbWindowedResolution.Items.Clear();

            int selectIndex = 0;
            for (int i = 0; i < resolutions.Count; i++)
            {
                var (w, h) = resolutions[i];
                cmbWindowedResolution.Items.Add(new ComboBoxItem
                {
                    Content = Loc.Ltr($"{w} x {h}"),
                    Tag = (w, h)
                });
                if (w == selectedWidth && h == selectedHeight)
                    selectIndex = i;
            }

            if (cmbWindowedResolution.Items.Count > 0)
                cmbWindowedResolution.SelectedIndex = selectIndex;
        }

        private void PopulateAnticheatPlugins()
        {
            _populatingPlugins = true;
            try
            {
                FillAnticheatPlugins();
            }
            finally
            {
                _populatingPlugins = false;
            }
        }

        private void FillAnticheatPlugins()
        {
            cmbAnticheatPlugin.Items.Clear();
            configDetailsPanel.Children.Clear();
            configDetailsPanel.Children.Add(new TextBlock 
            { 
                Text = Loc.T("(No plugin selected)"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
                FontSize = 11
            });
            
            try
            {
                string pluginsPath = Path.Combine(GamePaths.Game, "plugins");
                
                if (!Directory.Exists(pluginsPath))
                {
                    return;
                }

                var pluginFolders = Directory.GetDirectories(pluginsPath)
                    .Where(dir => Directory.GetFiles(dir, "*.json").Any())
                    .OrderBy(dir => new DirectoryInfo(dir).Name)
                    .ToList();

                foreach (var pluginDir in pluginFolders)
                {
                    string folderName = new DirectoryInfo(pluginDir).Name;
                    string displayName = folderName; // default to folder name
                    
                    // Try to extract plugin_name from JSON
                    try
                    {
                        var jsonFiles = Directory.GetFiles(pluginDir, "*.json")
                            .OrderBy(f => Path.GetFileName(f))
                            .FirstOrDefault();
                        
                        if (jsonFiles != null)
                        {
                            string jsonContent = File.ReadAllText(jsonFiles);
                            var jsonData = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonContent);
                            
                            if (jsonData != null && jsonData.TryGetValue("plugin_name", out var pluginNameObj))
                            {
                                displayName = pluginNameObj?.ToString() ?? folderName;
                            }
                        }
                    }
                    catch
                    {
                        // If JSON reading fails, just use folder name
                    }
                    
                    // Add to dropdown with folder name as Tag (for internal lookup)
                    cmbAnticheatPlugin.Items.Add(new ComboBoxItem 
                    { 
                        Content = displayName,
                        Tag = folderName
                    });
                }

                // Shows the plugin settings.json names; when it names none (or one that is not installed) nothing is
                // selected, and Generals Online keeps choosing for itself until the player picks one here
                int selectIndex = -1;
                for (int i = 0; i < cmbAnticheatPlugin.Items.Count; i++)
                {
                    if (cmbAnticheatPlugin.Items[i] is ComboBoxItem item && item.Tag?.ToString() == _anticheat)
                    {
                        selectIndex = i;
                        break;
                    }
                }
                cmbAnticheatPlugin.SelectedIndex = selectIndex;
                DisplayAnticheatPluginDetails();
            }
            catch
            {
                // silently fail if plugins folder doesn't exist or can't be read
            }
        }

        public OptionsPage()
        {
            InitializeComponent();
            Loaded += OptionsPage_Loaded;
        }

        // Opens a section by its sidebar name, e.g. "network" or "launcher" (used when taking screenshots)
        public void ShowPart(string part)
        {
            var tab = new[] { rbLanguage, rbGame, rbCamera, rbChat, rbInput, rbGraphics, rbSocial, rbNetwork, rbDataPacks, rbPlugins, rbLauncher }
                .FirstOrDefault(r => r.Name.Equals("rb" + part.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (tab != null)
                tab.IsChecked = true;
        }

        private void OptionsPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadGameSettings();
            LoadLauncherOptions();
            _ = ShowArabicGameTextAsync();
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelCamera == null) return; // not yet loaded

            PanelLanguage.Visibility = Visibility.Collapsed;
            PanelGame.Visibility = Visibility.Collapsed;
            PanelCamera.Visibility = Visibility.Collapsed;
            PanelChat.Visibility = Visibility.Collapsed;
            PanelInput.Visibility = Visibility.Collapsed;
            PanelGraphics.Visibility = Visibility.Collapsed;
            PanelSocial.Visibility = Visibility.Collapsed;
            PanelNetwork.Visibility = Visibility.Collapsed;
            PanelDataPacks.Visibility = Visibility.Collapsed;
            PanelPlugins.Visibility = Visibility.Collapsed;
            PanelLauncher.Visibility = Visibility.Collapsed;

            if (ReferenceEquals(sender, rbLanguage)) PanelLanguage.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbGame)) PanelGame.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbCamera)) PanelCamera.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbChat)) PanelChat.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbInput)) PanelInput.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbGraphics)) PanelGraphics.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbSocial)) PanelSocial.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbNetwork)) PanelNetwork.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbDataPacks)) PanelDataPacks.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, rbPlugins))
            {
                PanelPlugins.Visibility = Visibility.Visible;
                PopulateAnticheatPlugins();
            }
            else if (ReferenceEquals(sender, rbLauncher)) PanelLauncher.Visibility = Visibility.Visible;
        }

        private void ChkLimitFramerate_Changed(object sender, RoutedEventArgs e)
        {
            if (txtFPSLimit != null)
                txtFPSLimit.IsEnabled = chkLimitFramerate.IsChecked == true;
        }

        private void ChkWindowed_Changed(object sender, RoutedEventArgs e)
        {
            if (cmbWindowedResolution != null)
                cmbWindowedResolution.IsEnabled = chkWindowed.IsChecked == true;
        }

        private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private void CmbAnticheatPlugin_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_populatingPlugins)
                _anticheatTouched = true;
            DisplayAnticheatPluginDetails();
        }

        private void DisplayAnticheatPluginDetails()
        {
            configDetailsPanel.Children.Clear();

            if (cmbAnticheatPlugin.SelectedItem is not ComboBoxItem item)
            {
                configDetailsPanel.Children.Add(new TextBlock 
                { 
                    Text = Loc.T("(No plugin selected)"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
                    FontSize = 11
                });
                return;
            }

            // Use Tag (folder name) for file lookup, not Content (display name)
            string? folderName = item.Tag?.ToString();
            if (string.IsNullOrEmpty(folderName))
            {
                configDetailsPanel.Children.Add(new TextBlock 
                { 
                    Text = Loc.T("(Invalid plugin folder)"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88)),
                    FontSize = 11
                });
                return;
            }

            try
            {
                string pluginPath = Path.Combine(GamePaths.Game, "plugins", folderName);
                
                if (!Directory.Exists(pluginPath))
                {
                    configDetailsPanel.Children.Add(new TextBlock 
                    { 
                        Text = Loc.T("(Plugin folder not found: {0})", folderName),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88)),
                        FontSize = 11
                    });
                    return;
                }

                var jsonFiles = Directory.GetFiles(pluginPath, "*.json")
                    .OrderBy(f => Path.GetFileName(f))
                    .ToList();

                if (jsonFiles.Count == 0)
                {
                    configDetailsPanel.Children.Add(new TextBlock 
                    { 
                        Text = Loc.T("(No JSON files found in plugin)"),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
                        FontSize = 11
                    });
                    return;
                }

                // Parse and display each JSON file
                foreach (var jsonFile in jsonFiles)
                {
                    string fileName = Path.GetFileName(jsonFile);
                    
                    try
                    {
                        string jsonContent = File.ReadAllText(jsonFile);
                        var jsonData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(jsonContent);
                        
                        if (jsonData != null)
                        {
                            AddFileSection(fileName, jsonData);
                        }
                    }
                    catch (Exception ex)
                    {
                        configDetailsPanel.Children.Add(new TextBlock 
                        { 
                            Text = Loc.T("Could not read {0}: {1}", fileName, ex.Message),
                            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88)),
                            FontSize = 10,
                            Margin = new Thickness(0, 8, 0, 8)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                configDetailsPanel.Children.Add(new TextBlock 
                { 
                    Text = Loc.T("(Could not load the plugin details: {0})", ex.Message),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88)),
                    FontSize = 11
                });
            }
        }

        private void AddFileSection(string fileName, Dictionary<string, object> data)
        {
            var filePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            // Use plugin_name field if available, otherwise use filename
            string headerText = fileName;
            if (data.TryGetValue("plugin_name", out var pluginNameObj) && pluginNameObj != null)
            {
                headerText = pluginNameObj.ToString() ?? headerText;
            }

            // File name header
            var fileHeader = new TextBlock
            {
                Text = headerText,
                Foreground = new SolidColorBrush(Color.FromRgb(0x41, 0xB6, 0xFF)),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            filePanel.Children.Add(fileHeader);

            // Properties grid
            var propertiesPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            
            foreach (var kvp in data.OrderBy(x => x.Key))
            {
                // Skip plugin_name if it was used as header
                if (kvp.Key == "plugin_name" && data.TryGetValue("plugin_name", out var _))
                    continue;
                
                AddPropertyRow(propertiesPanel, kvp.Key, kvp.Value, indent: 0);
            }

            filePanel.Children.Add(propertiesPanel);
            configDetailsPanel.Children.Add(filePanel);
        }

        private void AddPropertyRow(StackPanel container, string key, object value, int indent)
        {
            var rowPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(indent * 12, 4, 0, 4)
            };

            // Property name - convert to human readable format
            string displayKey = ToHumanReadableName(key);
            var keyBlock = new TextBlock
            {
                Text = displayKey + ":",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xBB, 0xFF)),
                FontSize = 11,
                MinWidth = 140,
                VerticalAlignment = VerticalAlignment.Top
            };
            rowPanel.Children.Add(keyBlock);

            // Property value
            if (value is Dictionary<string, object> dict)
            {
                var nestedPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
                var nestedHeader = new TextBlock
                {
                    Text = "{",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
                    FontSize = 11
                };
                nestedPanel.Children.Add(nestedHeader);

                foreach (var nestedKvp in dict.OrderBy(x => x.Key))
                {
                    AddPropertyRow(nestedPanel, nestedKvp.Key, nestedKvp.Value, indent: 1);
                }

                var closeBrace = new TextBlock
                {
                    Text = "}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
                    FontSize = 11
                };
                nestedPanel.Children.Add(closeBrace);
                rowPanel.Children.Add(nestedPanel);
            }
            else
            {
                var valueBlock = new TextBlock
                {
                    Text = FormatValue(value),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xC0, 0x88)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };
                rowPanel.Children.Add(valueBlock);
            }

            container.Children.Add(rowPanel);
        }

        private string FormatValue(object value)
        {
            if (value == null)
                return "null";
            
            if (value is JsonElement elem)
            {
                return elem.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => $"\"{elem.GetString()}\"",
                    System.Text.Json.JsonValueKind.Number => elem.GetRawText(),
                    System.Text.Json.JsonValueKind.True => "true",
                    System.Text.Json.JsonValueKind.False => "false",
                    _ => elem.GetRawText()
                };
            }

            if (value is string str)
                return $"\"{str}\"";
            
            if (value is bool b)
                return b ? "true" : "false";
            
            if (value is JsonElement)
                return value.ToString() ?? "";

            return value.ToString() ?? "";
        }

        private string ToHumanReadableName(string fieldName)
        {
            // Convert snake_case to Title Case
            // e.g., "plugin_author" -> "Plugin Author"
            //       "last_update" -> "Last Update"
            //       "version" -> "Version"
            return string.Join(" ", 
                fieldName.Split('_')
                    .Select(word => char.ToUpper(word[0]) + word.Substring(1).ToLower()));
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            SaveGameSettings();
            Views.Main.ShowHome();
        }

        private static int ParseInt(TextBox tb, int defaultVal)
        {
            return int.TryParse(tb.Text, out int v) ? v : defaultVal;
        }

        // Whole numbers as the text box shows them; the box takes digits only
        private static string Whole(double value) => ((long)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static string Check(CheckBox box) => box.IsChecked == true ? "1" : "0";

        // Reads settings.json, launcher.json and Options.ini into the page. A value that is missing or of an unexpected
        // type shows its default; nothing is written here.
        public void LoadGameSettings()
        {
            try
            {
                var go = GoSettings.Load();

                txtMaxCameraHeight.Text = Whole(go.Get("camera", "max_height_only_when_lobby_host", 310.0));
                txtMinCameraHeight.Text = Whole(go.Get("camera", "min_height", 210.0));
                txtCameraMovementSpeed.Text = Whole(go.Get("camera", "move_speed_ratio", 1.0) * 100);

                txtChatDuration.Text = Whole(go.Get("chat", "duration_seconds_until_fade_out", 30.0));

                chkLimitFramerate.IsChecked = go.Get("render", "limit_framerate", true);
                txtFPSLimit.Text = Whole(go.Get("render", "fps_limit", 60.0));
                txtFPSLimit.IsEnabled = chkLimitFramerate.IsChecked == true;
                chkStatsOverlay.IsChecked = go.Get("render", "stats_overlay", true);

                var launcher = LauncherJson.Load();
                chkWindowed.IsChecked = launcher.Windowed;
                PopulateResolutions(launcher.WindowedWidth, launcher.WindowedHeight);
                cmbWindowedResolution.IsEnabled = launcher.Windowed;

                chkFriendOnlineInGame.IsChecked = go.Get("social", "notification_friend_comes_online_gameplay", true);
                chkFriendOnlineInMenus.IsChecked = go.Get("social", "notification_friend_comes_online_menus", true);
                chkFriendOfflineInGame.IsChecked = go.Get("social", "notification_friend_goes_offline_gameplay", true);
                chkFriendOfflineInMenus.IsChecked = go.Get("social", "notification_friend_goes_offline_menus", true);
                chkFriendRequestInGame.IsChecked = go.Get("social", "notification_player_sends_request_gameplay", true);
                chkFriendRequestInMenus.IsChecked = go.Get("social", "notification_player_sends_request_menus", true);
                chkFriendAcceptedInGame.IsChecked = go.Get("social", "notification_player_accepts_request_gameplay", true);
                chkFriendAcceptedInMenus.IsChecked = go.Get("social", "notification_player_accepts_request_menus", true);

                int httpVer = (int)go.Get("network", "http_version", 0.0);
                cmbHTTPVersion.SelectedIndex = httpVer >= 0 && httpVer < cmbHTTPVersion.Items.Count ? httpVer : 0;
                chkAlternativeEndpoint.IsChecked = go.Get("network", "use_alternative_endpoint", false);

                chkUseCommunityDataPatch.IsChecked = go.Get("data_packs", "use_community_data_patch", true);

                _anticheat = go.AntiCheat;
                _anticheatTouched = false;

                LoadIniSettings();

                _shown = Shown();
                _loaded = true;
            }
            catch (Exception ex)
            {
                // Saving stays off, so nothing is written from values that were not read
                _loaded = false;
                Views.Main.Toast(Loc.T("Some settings could not be read, so this page will not save anything: {0}", ex.Message), isError: true);
            }
        }

        // The game's defaults for keys Options.ini does not have yet
        private void LoadIniSettings()
        {
            var ini = OptionsFile.Read();
            bool GetBool(string key, bool fallback) => ini.TryGetValue(key, out var v) ? v.Equals("yes", StringComparison.OrdinalIgnoreCase) : fallback;

            chkCursorLockFullscreen.IsChecked = GetBool("CursorCaptureEnabledInFullscreenGame", true) ||
                                                GetBool("CursorCaptureEnabledInFullscreenMenu", true);
            chkCursorLockWindowed.IsChecked = GetBool("CursorCaptureEnabledInWindowedGame", true) ||
                                              GetBool("CursorCaptureEnabledInWindowedMenu", false);
            chkEdgeScrollingFullscreen.IsChecked = GetBool("ScreenEdgeScrollEnabledInFullscreenApp", true);
            chkEdgeScrollingWindowed.IsChecked = GetBool("ScreenEdgeScrollEnabledInWindowedApp", false);
        }

        // settings.json fields on this page: the text its control shows, and the value it stands for
        private (string Section, string Key, string Shown, Func<JsonNode?> Value)[] GoFields() => new (string, string, string, Func<JsonNode?>)[]
        {
            ("camera", "max_height_only_when_lobby_host", txtMaxCameraHeight.Text, () => JsonValue.Create(ParseInt(txtMaxCameraHeight, 310))),
            ("camera", "min_height", txtMinCameraHeight.Text, () => JsonValue.Create(ParseInt(txtMinCameraHeight, 210))),
            ("camera", "move_speed_ratio", txtCameraMovementSpeed.Text, () => JsonValue.Create(ParseInt(txtCameraMovementSpeed, 100) / 100.0)),
            ("chat", "duration_seconds_until_fade_out", txtChatDuration.Text, () => JsonValue.Create(ParseInt(txtChatDuration, 30))),
            ("render", "limit_framerate", Check(chkLimitFramerate), () => JsonValue.Create(chkLimitFramerate.IsChecked == true)),
            ("render", "fps_limit", txtFPSLimit.Text, () => JsonValue.Create(ParseInt(txtFPSLimit, 60))),
            ("render", "stats_overlay", Check(chkStatsOverlay), () => JsonValue.Create(chkStatsOverlay.IsChecked == true)),
            ("social", "notification_friend_comes_online_gameplay", Check(chkFriendOnlineInGame), () => JsonValue.Create(chkFriendOnlineInGame.IsChecked == true)),
            ("social", "notification_friend_comes_online_menus", Check(chkFriendOnlineInMenus), () => JsonValue.Create(chkFriendOnlineInMenus.IsChecked == true)),
            ("social", "notification_friend_goes_offline_gameplay", Check(chkFriendOfflineInGame), () => JsonValue.Create(chkFriendOfflineInGame.IsChecked == true)),
            ("social", "notification_friend_goes_offline_menus", Check(chkFriendOfflineInMenus), () => JsonValue.Create(chkFriendOfflineInMenus.IsChecked == true)),
            ("social", "notification_player_sends_request_gameplay", Check(chkFriendRequestInGame), () => JsonValue.Create(chkFriendRequestInGame.IsChecked == true)),
            ("social", "notification_player_sends_request_menus", Check(chkFriendRequestInMenus), () => JsonValue.Create(chkFriendRequestInMenus.IsChecked == true)),
            ("social", "notification_player_accepts_request_gameplay", Check(chkFriendAcceptedInGame), () => JsonValue.Create(chkFriendAcceptedInGame.IsChecked == true)),
            ("social", "notification_player_accepts_request_menus", Check(chkFriendAcceptedInMenus), () => JsonValue.Create(chkFriendAcceptedInMenus.IsChecked == true)),
            ("network", "http_version", cmbHTTPVersion.SelectedIndex.ToString(), () => JsonValue.Create(cmbHTTPVersion.SelectedIndex)),
            ("network", "use_alternative_endpoint", Check(chkAlternativeEndpoint), () => JsonValue.Create(chkAlternativeEndpoint.IsChecked == true)),
            ("data_packs", "use_community_data_patch", Check(chkUseCommunityDataPatch), () => JsonValue.Create(chkUseCommunityDataPatch.IsChecked == true)),
        };

        private string ResolutionShown() => cmbWindowedResolution.SelectedItem is ComboBoxItem { Tag: (int w, int h) } ? $"{w}x{h}" : "";

        // Every control's current text, keyed like _shown
        private Dictionary<string, string> Shown()
        {
            var shown = GoFields().ToDictionary(f => f.Section + "/" + f.Key, f => f.Shown);
            shown["launcher/windowed"] = Check(chkWindowed);
            shown["launcher/resolution"] = ResolutionShown();
            shown["ini/cursorFull"] = Check(chkCursorLockFullscreen);
            shown["ini/cursorWin"] = Check(chkCursorLockWindowed);
            shown["ini/edgeFull"] = Check(chkEdgeScrollingFullscreen);
            shown["ini/edgeWin"] = Check(chkEdgeScrollingWindowed);
            return shown;
        }

        private bool Changed(Dictionary<string, string> now, string key) =>
            _shown.TryGetValue(key, out var was) && now.TryGetValue(key, out var value) && was != value;

        // Saves what was set on this page; the window calls it when the page is left by a tab or closed
        public void Commit()
        {
            if (IsLoaded)
                SaveGameSettings();
        }

        // Writes only what the player changed on this page since it was loaded
        private void SaveGameSettings()
        {
            if (!_loaded)
                return;

            var now = Shown();
            bool patchChanged = Changed(now, "data_packs/use_community_data_patch");

            // settings.json is edited as a JSON tree, so fields added by newer Generals Online versions stay as they are
            var go = GoSettings.Load();
            bool goChanged = ApplyGoChanges(go, now);
            var ini = IniChanges(now);
            bool iniChanged = OptionsFile.WouldChange(ini);
            bool launcherChanged = Changed(now, "launcher/windowed") || Changed(now, "launcher/resolution");

            // The game reads settings.json and Options.ini when it starts and writes Options.ini itself. The changes stay
            // on the page and are saved the next time it is left with the game closed.
            if ((goChanged || iniChanged) && GameLauncher.IsGameRunning())
            {
                Views.Main.Toast(Loc.T("Close the game first; it reads these settings only when it starts."), isError: true);
                return;
            }

            try
            {
                if (goChanged)
                    go.Save("Saved from the Options page");
                if (iniChanged)
                    OptionsFile.Write(ini, "Game options", "Saved from the Options page");
                if (launcherChanged)
                    SaveWindowedToLauncherJson();
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Could not save: {0}", ex.Message), isError: true);
                return;
            }
            _shown = now;
            _anticheatTouched = false;
            if (goChanged)
                _anticheat = go.AntiCheat;

            if (patchChanged)
            {
                // The hotkey letters on the pictures follow the buttons the game will show
                AppState.ForgetHotkeys();
                if (AppSettings.Current.IconLetters)
                    _ = Task.Run(async () =>
                    {
                        if (await AppState.Hotkeys() is { } hotkeys)
                            IconLettersService.RefreshIfEnabled(hotkeys);
                    });
            }
        }

        // ── LAUNCHER: Command Center's own preferences ──

        private bool _loadingLauncher;

        private void LoadLauncherOptions()
        {
            _loadingLauncher = true;
            // The tab keeps its English name next to the translation, so it can be found in a language one cannot read
            rbLanguage.Content = Loc.Language == "en" ? "LANGUAGE" : Loc.T("LANGUAGE") + " · LANGUAGE";
            cmbLanguage.Items.Clear();
            foreach (var (code, name) in Loc.Languages)
            {
                var item = new ComboBoxItem { Content = name, Tag = code };
                cmbLanguage.Items.Add(item);
                if (code == Loc.Language)
                    cmbLanguage.SelectedItem = item;
            }
            txtVersion.Text = "Command Center " + AppState.Version;
            _loadingLauncher = false;
            ShowGameFolder();
        }

        // ── GAME FOLDER: which Zero Hour is played, and where the game keeps its data ──

        private void ShowGameFolder()
        {
            bool found = GamePaths.ZeroHourFound;
            string folder = GamePaths.Game;
            txtGameFolder.Text = found ? Loc.Ltr(folder) : Loc.T("Game not found");
            txtGameFolder.ToolTip = found ? folder : null;

            (string badge, Color color) = !found ? (Loc.T("Not found"), Views.Problem.Color)
                : GamePaths.ChosenByPlayer ? (Loc.T("Chosen by you"), Views.Blue.Color)
                : GamePaths.IsSteamCopy(folder) ? (Loc.T("Steam copy"), Views.Passed.Color)
                : (Loc.T("Other copy"), Views.Gold.Color);
            SourceBadgeText.Text = badge;
            SourceBadgeText.Foreground = new SolidColorBrush(color);
            SourceBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));

            GameChips.Children.Clear();
            if (found)
            {
                GameChips.Children.Add(Chip("Zero Hour", true));
                GameChips.Children.Add(GamePaths.GameFound
                    ? Chip("Generals Online " + AppState.ClientVersion(), true)
                    : Chip(Loc.T("Generals Online is not installed"), false));
            }
            txtGameNote.Text = !found ? Loc.T("Choose the folder of any copy of Zero Hour: Steam, EA or another install.")
                : GamePaths.GameFound ? Loc.T("Any copy works: Steam, EA or another install. Changing the folder changes nothing in the game files.")
                : Loc.T("Install Generals Online into this folder, then press Play.");

            txtDataFolder.Text = Loc.Ltr(GamePaths.UserData);
            txtDataFolder.ToolTip = GamePaths.UserData;
        }

        private static Border Chip(string text, bool ok)
        {
            var color = ok ? Views.Passed.Color : Views.Problem.Color;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Controls.Icon
            {
                Kind = ok ? "check" : "close",
                Width = 11,
                Height = 11,
                StrokeThickness = 3,
                Foreground = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 6, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = Loc.Embed(text),
                FontSize = 12,
                Foreground = Views.Brush(ok ? "#C6EED2" : "#FFC2C2"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return new Border
            {
                Child = row,
                Height = 28,
                Padding = new Thickness(10, 0, 11, 0),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(0x14, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
            };
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!Views.ChooseGameFolder(Window.GetWindow(this)))
                return;
            LoadGameSettings();
            ShowGameFolder();
            Views.Main.Toast(Loc.T("Game folder changed. The checks run again."));
        }

        private void OpenData_Click(object sender, RoutedEventArgs e)
        {
            Views.ShowInFolder(GamePaths.UserData);
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingLauncher || cmbLanguage.SelectedItem is not ComboBoxItem { Tag: string code } || code == Loc.Language)
                return;
            var answer = MessageBox.Show(Loc.T("Restart Command Center now to switch the language?"), "Command Center",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes, Loc.MessageBoxOptions);
            if (answer != MessageBoxResult.Yes)
            {
                LoadLauncherOptions();
                return;
            }
            AppSettings.Current.Language = code;
            AppSettings.Current.Save();
            SaveGameSettings();

            // Same arguments as this run, without a language given on the command line
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            int lang = args.FindIndex(a => a.Equals("--lang", StringComparison.OrdinalIgnoreCase));
            if (lang >= 0)
                args.RemoveRange(lang, Math.Min(2, args.Count - lang));
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (string arg in args)
                start.ArgumentList.Add(arg);
            System.Diagnostics.Process.Start(start)?.Dispose();
            Application.Current.Shutdown();
        }

        // ─── Game text in Arabic ─────────────────────────────────────────────────

        private bool _arabicBusy;

        private static (GameFiles Files, string Language)? ArabicTarget() =>
            GamePaths.ZeroHourFound ? (new GameFiles(GamePaths.Game), HotkeyService.DetectLanguage()) : null;

        private async Task ShowArabicGameTextAsync()
        {
            var state = await Task.Run(() =>
            {
                try { return ArabicTarget() is { } t ? ArabicGameText.Check(t.Files, t.Language) : ArabicGameText.State.NoGame; }
                catch { return ArabicGameText.State.NoGame; }
            });
            int count = ArabicGameText.Translation.Text.Count;
            txtArabicState.Text = state switch
            {
                ArabicGameText.State.On => Loc.T("On. Start the game to see it."),
                ArabicGameText.State.Outdated => Loc.T("The game's text changed since it was turned on. Turn it on again to update it."),
                ArabicGameText.State.GameHasArabic => Loc.T("The game already shows Arabic text."),
                ArabicGameText.State.NoGame => Loc.T("Zero Hour was not found."),
                _ => Loc.T("Off: the game's text is in English."),
            };
            btnArabicOn.Content = Loc.T(state == ArabicGameText.State.Outdated ? "UPDATE" : "TURN ON");
            btnArabicOn.Visibility = state is ArabicGameText.State.Off or ArabicGameText.State.Outdated ? Visibility.Visible : Visibility.Collapsed;
            btnArabicOff.Visibility = state is ArabicGameText.State.On or ArabicGameText.State.Outdated ? Visibility.Visible : Visibility.Collapsed;
            btnArabicOn.IsEnabled = btnArabicOff.IsEnabled = !_arabicBusy;

            panelArabicNotes.Children.Clear();
            foreach (string note in new[]
                     {
                         Loc.N(count, "{0} text so far: the buttons, unit names, battle messages and menus seen most. More come with updates.",
                             "{0} texts so far: the buttons, unit names, battle messages and menus seen most. More come with updates."),
                         Loc.T("Hotkeys keep working: each button shows its key in brackets after the Arabic name."),
                         Loc.T("Not part of the multiplayer check, so it never causes a mismatch."),
                         Loc.T("Generals Online's own screens (login, lobby, chat) stay in English, and Arabic player names still show reversed: those are drawn by its program."),
                     })
            {
                panelArabicNotes.Children.Add(new TextBlock { Text = "• " + note, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xC8)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
            }
        }

        private void ArabicOn_Click(object sender, RoutedEventArgs e) => _ = SwitchArabicAsync(on: true);

        private void ArabicOff_Click(object sender, RoutedEventArgs e) => _ = SwitchArabicAsync(on: false);

        private async Task SwitchArabicAsync(bool on)
        {
            if (_arabicBusy || ArabicTarget() is not { } target)
                return;
            if (GameLauncher.IsGameRunning())
            {
                Views.Main.Toast(Loc.T(BackupService.GameRunningMessage), isError: true);
                return;
            }
            _arabicBusy = true;
            btnArabicOn.IsEnabled = btnArabicOff.IsEnabled = false;
            try
            {
                int count = await Task.Run(() =>
                {
                    if (!on)
                    {
                        ArabicGameText.Remove(target.Files, target.Language);
                        return 0;
                    }
                    return ArabicGameText.Apply(target.Files, target.Language);
                });
                // The hotkey editor reads the table again, Arabic names and all
                AppState.ForgetHotkeys();
                if (on)
                    Views.Main.Toast(Loc.N(count, "The game shows {0} text in Arabic from its next start.", "The game shows {0} texts in Arabic from its next start."),
                        () => _ = SwitchArabicAsync(on: false));
                else
                    Views.Main.Toast(Loc.T("The game's text is in English again from its next start."), () => _ = SwitchArabicAsync(on: true));
            }
            catch (Exception ex)
            {
                Views.Main.Toast(Loc.T("Something went wrong: {0}", ex.Message), isError: true);
            }
            finally
            {
                _arabicBusy = false;
                await ShowArabicGameTextAsync();
            }
        }

        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            btnCheckUpdates.IsEnabled = false;
            txtUpdateStatus.Text = Loc.T("Checking…");
            bool found = await Views.Main.CheckForUpdateNowAsync();
            txtUpdateStatus.Text = found ? "" : Loc.T("You have the latest version.");
            btnCheckUpdates.IsEnabled = true;
        }

        // launcher.json is edited as a JSON tree, so the client choice and anything else in it stays as it was
        private void SaveWindowedToLauncherJson()
        {
            var launcher = LauncherJson.Load();
            launcher.Windowed = chkWindowed.IsChecked == true;
            if (cmbWindowedResolution.SelectedItem is ComboBoxItem item && item.Tag is (int w, int h))
            {
                launcher.WindowedWidth = w;
                launcher.WindowedHeight = h;
            }
            launcher.Save("Saved from the Options page");
        }

        // Puts the values the player changed into the settings.json tree; true when that changes anything
        private bool ApplyGoChanges(GoSettings go, Dictionary<string, string> now)
        {
            string before = go.Root.ToJsonString();
            foreach (var field in GoFields())
                if (Changed(now, field.Section + "/" + field.Key))
                    go.SetNode(field.Section, field.Key, field.Value());
            if (_anticheatTouched && cmbAnticheatPlugin.SelectedItem is ComboBoxItem { Tag: string plugin } && plugin.Length > 0)
                go.Set("plugins", "anticheat", plugin);
            return go.Root.ToJsonString() != before;
        }

        // The Options.ini keys behind the boxes the player changed; one box sets both the game and the menu key
        private Dictionary<string, string> IniChanges(Dictionary<string, string> now)
        {
            var changes = new Dictionary<string, string>();
            if (Changed(now, "ini/edgeFull"))
                changes["ScreenEdgeScrollEnabledInFullscreenApp"] = Y(chkEdgeScrollingFullscreen.IsChecked == true);
            if (Changed(now, "ini/edgeWin"))
                changes["ScreenEdgeScrollEnabledInWindowedApp"] = Y(chkEdgeScrollingWindowed.IsChecked == true);
            if (Changed(now, "ini/cursorFull"))
            {
                changes["CursorCaptureEnabledInFullscreenGame"] = Y(chkCursorLockFullscreen.IsChecked == true);
                changes["CursorCaptureEnabledInFullscreenMenu"] = Y(chkCursorLockFullscreen.IsChecked == true);
            }
            if (Changed(now, "ini/cursorWin"))
            {
                changes["CursorCaptureEnabledInWindowedGame"] = Y(chkCursorLockWindowed.IsChecked == true);
                changes["CursorCaptureEnabledInWindowedMenu"] = Y(chkCursorLockWindowed.IsChecked == true);
            }
            return changes;
        }

        private static string Y(bool b) => b ? "yes" : "no";

        // ─── Network Diagnostics ─────────────────────────────────────────────────

        private async void BtnRunDiagnostics_Click(object sender, RoutedEventArgs e) => await RunDiagnosticsAsync();

        // Also used by --capture with "options:network:run"
        public async Task RunDiagnosticsAsync()
        {
            _diagCts?.Cancel();
            _diagCts = new CancellationTokenSource();

            btnRunDiagnostics.IsEnabled = false;
            btnRunDiagnostics.Content   = Loc.T("⟳  RUNNING...");
            btnCopyDiag.IsEnabled       = false;
            panelDiagResults.Visibility = Visibility.Visible;
            ResetDiagnosticsUI();

            try
            {
                var result = await new NetworkDiagnosticsService().RunAsync(_diagCts.Token);
                _lastDiagResult = result;
                UpdateDiagnosticsUI(result);
                AppendDiagLog(result);
                btnCopyDiag.IsEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                btnRunDiagnostics.IsEnabled = true;
                btnRunDiagnostics.Content   = Loc.T("▶  RUN DIAGNOSTICS");
            }
        }

        private void BtnCopyDiag_Click(object sender, RoutedEventArgs e)
        {
            if (_lastDiagResult is not { } r) return;
            var sb = new StringBuilder();
            sb.AppendLine("═══ Generals Online Network Diagnostics ═══");
            sb.AppendLine($"Time:         {r.Timestamp:yyyy-MM-dd HH:mm:ss}");
            if (r.GeoSuccess)
                sb.AppendLine($"Country:      {CountryName(r.GeoCountryCode, english: true)}");
            sb.AppendLine();
            sb.AppendLine("─ Internet ─");
            sb.AppendLine($"Cloudflare:   {(r.CloudflareSuccess ? $"{r.CloudflareAvgMs}ms avg, {r.CloudflareLossPercent}% loss" : "Unreachable")}");
            sb.AppendLine();
            sb.AppendLine("─ Game Server ─");
            sb.AppendLine($"DNS:          {(r.DnsSuccess ? $"OK — {r.DnsAddresses}" : "FAILED")}");
            sb.AppendLine($"Ping (ICMP):  {(r.PingSuccess ? $"{r.PingAvgMs}ms avg (min {r.PingMinMs} / max {r.PingMaxMs}), {r.PingLossPercent}% loss" : "Unreachable")}");
            sb.AppendLine($"Protocol:     {r.Protocol}");
            sb.AppendLine($"HTTP Latency: {(r.HttpSuccess ? $"{r.HttpLatencyMs}ms" : "Failed")}");
            sb.AppendLine($"Server:       {(r.ServerOnline ? $"Online — {r.PlayersOnline} players, {r.Lobbies} lobbies" : "Offline / Unreachable")}");
            sb.AppendLine();
            sb.AppendLine("─ Connectivity ─");
            sb.AppendLine($"CDN:          {(r.CdnSuccess ? $"{r.CdnLatencyMs}ms" : "Unreachable")}");
            sb.AppendLine($"STUN:         {(r.StunSuccess ? $"OK — {RedactEndpoint(r.StunExternalEndpoint)} ({r.StunLatencyMs}ms)" : "Unreachable")}");
            sb.AppendLine($"TURN:         {(r.TurnSuccess ? $"OK — {r.TurnEndpoint} ({r.TurnLatencyMs}ms)" : "Unreachable")}");
            sb.AppendLine($"NAT Type:     {r.NatType} — {r.NatTypeDetail}");
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        private void ResetDiagnosticsUI()
        {
            var gray = DiagGray;
            foreach (var dot in new[] { dotGeo, dotCF, dotDns, dotPing, dotProto, dotHttp, dotServer, dotCdn, dotStun, dotTurn, dotNat })
                dot.Fill = gray;
            foreach (var val in new[] { valGeo, valCF, valDns, valPing, valProto, valHttp, valServer, valCdn, valStun, valTurn, valNat })
            {
                val.Text       = "checking…";
                val.Foreground = gray;
            }
        }

        private void UpdateDiagnosticsUI(NetworkDiagnosticsResult r)
        {
            // Country
            if (r.GeoSuccess)
                SetRow(dotGeo, valGeo, true, CountryName(r.GeoCountryCode, english: !Loc.IsRightToLeft), DiagGreen);
            else
                SetRow(dotGeo, valGeo, false, Loc.T("Could not determine the country"), DiagGray);

            // Cloudflare internet baseline
            SetRow(dotCF, valCF,
                r.CloudflareSuccess,
                r.CloudflareSuccess
                    ? Loc.T("{0} ms avg  {1}% loss", r.CloudflareAvgMs, r.CloudflareLossPercent)
                    : Loc.T("Unreachable"),
                r.CloudflareSuccess ? LossColor(r.CloudflareLossPercent) : DiagRed);

            // DNS
            SetRow(dotDns, valDns,
                r.DnsSuccess,
                r.DnsSuccess ? r.DnsAddresses : Loc.T("Resolution failed"),
                r.DnsSuccess ? DiagGreen : DiagRed);

            // Ping
            SetRow(dotPing, valPing,
                r.PingSuccess,
                r.PingSuccess
                    ? Loc.T("{0} ms avg  (min {1} / max {2})  {3}% loss", r.PingAvgMs, r.PingMinMs, r.PingMaxMs, r.PingLossPercent)
                    : Loc.T("Unreachable (ICMP may be blocked)"),
                r.PingSuccess ? LatencyColor(r.PingAvgMs) : DiagRed);

            // Protocol
            bool protoOk = r.Protocol is "IPv4" or "IPv6";
            SetRow(dotProto, valProto, protoOk, r.Protocol, protoOk ? DiagGreen : DiagGray);

            // HTTP latency
            SetRow(dotHttp, valHttp,
                r.HttpSuccess,
                r.HttpSuccess ? $"{r.HttpLatencyMs}ms" : Loc.T("Request failed"),
                r.HttpSuccess ? LatencyColor(r.HttpLatencyMs) : DiagRed);

            // Server status
            if (r.ServerOnline)
                SetRow(dotServer, valServer, true,
                    Loc.T("Online — {0} players  •  {1} lobbies", r.PlayersOnline, r.Lobbies), DiagGreen);
            else
                SetRow(dotServer, valServer, false,
                    r.HttpSuccess ? Loc.T("Degraded (HTTP error)") : Loc.T("Offline / Unreachable"), DiagRed);

            // CDN
            SetRow(dotCdn, valCdn,
                r.CdnSuccess,
                r.CdnSuccess ? $"{r.CdnLatencyMs}ms" : Loc.T("Unreachable"),
                r.CdnSuccess ? LatencyColor(r.CdnLatencyMs) : DiagRed);

            // STUN
            SetRow(dotStun, valStun,
                r.StunSuccess,
                r.StunSuccess ? $"{r.StunLatencyMs}ms  ({RedactEndpoint(r.StunExternalEndpoint)})" : Loc.T("Unreachable"),
                r.StunSuccess ? LatencyColor(r.StunLatencyMs) : DiagRed);

            // TURN
            SetRow(dotTurn, valTurn,
                r.TurnSuccess,
                r.TurnSuccess ? $"{r.TurnLatencyMs}ms  ({r.TurnEndpoint})" : Loc.T("Unreachable"),
                r.TurnSuccess ? LatencyColor(r.TurnLatencyMs) : DiagRed);

            // NAT type
            // The detail stays English in the copied report for support, and is translated here
            string natDetail = Loc.T(r.NatTypeDetail);
            var (natColor, natText) = r.NatType switch
            {
                NatType.Open or NatType.FullCone or NatType.RestrictedCone
                    => (DiagGreen,  $"{NatTypeLabel(r.NatType)} — {natDetail}"),
                NatType.PortRestrictedCone
                    => (DiagYellow, Loc.T("Port Restricted Cone") + $" — {natDetail}"),
                NatType.Symmetric
                    => (DiagRed,    Loc.T("Symmetric") + $" — {natDetail}"),
                _   => (DiagGray,   Loc.T("Unknown") + $" — {natDetail}")
            };
            SetRow(dotNat, valNat, r.NatType != NatType.Unknown, natText, natColor);
        }

        private void AppendDiagLog(NetworkDiagnosticsResult r)
        {
            const int MaxEntries = 10;

            string entry = $"[{r.Timestamp:HH:mm:ss}]  " +
                $"CF:{(r.CloudflareSuccess ? $"{r.CloudflareAvgMs}ms" : "✗")}  |  " +
                $"Ping: {(r.PingSuccess ? $"{r.PingAvgMs}ms {r.PingLossPercent}% loss" : "✗")}  |  " +
                $"HTTP: {(r.HttpSuccess ? $"{r.HttpLatencyMs}ms" : "✗")}  |  " +
                $"STUN: {(r.StunSuccess ? "✓" : "✗")}  |  " +
                $"TURN: {(r.TurnSuccess ? "✓" : "✗")}  |  " +
                $"NAT: {NatTypeLabel(r.NatType)}  |  " +
                $"{(r.ServerOnline ? "Online" : "Offline")}";

            diagLog.Children.Insert(0, new TextBlock
            {
                Text        = entry,
                Foreground  = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x99)),
                FontSize    = 11,
                FontFamily  = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Margin      = new Thickness(0, 0, 0, 3)
            });

            while (diagLog.Children.Count > MaxEntries)
                diagLog.Children.RemoveAt(diagLog.Children.Count - 1);
        }

        private static void SetRow(Ellipse dot, TextBlock val, bool ok, string text, SolidColorBrush color)
        {
            dot.Fill       = color;
            // A value with no Arabic in it, such as "93ms  (1.2.3.XXX:4)", keeps its brackets in order
            val.Text       = text.Any(c => c is >= '؀' and <= 'ۿ') ? text : Loc.Ltr(text);
            val.Foreground = color;
        }

        private static SolidColorBrush LatencyColor(int ms) =>
            ms <= 60 ? DiagGreen : ms <= 150 ? DiagYellow : DiagRed;

        private static SolidColorBrush LossColor(int pct) =>
            pct == 0 ? DiagGreen : pct < 25 ? DiagYellow : DiagRed;

        // The country's English name; in Arabic, its own name when that is written in Arabic letters
        private static string CountryName(string code, bool english)
        {
            try
            {
                var region = new System.Globalization.RegionInfo(code);
                return !english && region.NativeName.Any(c => c is >= '؀' and <= 'ۿ') ? region.NativeName : region.EnglishName;
            }
            catch (ArgumentException) { return code; }
        }

        private static string NatTypeLabel(NatType t) => t switch
        {
            NatType.Open              => Loc.T("Open"),
            NatType.FullCone          => Loc.T("Full Cone"),
            NatType.RestrictedCone    => Loc.T("Restricted"),
            NatType.PortRestrictedCone => Loc.T("Port Restr."),
            NatType.Symmetric         => Loc.T("Symmetric"),
            _                         => Loc.T("Unknown")
        };

        private static string RedactIp(string ip)
        {
            int last = ip.LastIndexOf('.');
            return last >= 0 ? ip[..last] + ".XXX" : ip;
        }

        // Redacts the IP portion of an "ip:port" endpoint string
        private static string RedactEndpoint(string endpoint)
        {
            int colon = endpoint.LastIndexOf(':');
            string ip   = colon >= 0 ? endpoint[..colon] : endpoint;
            string port = colon >= 0 ? endpoint[colon..]  : "";
            return RedactIp(ip) + port;
        }
    }
}
