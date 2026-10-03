using CommandCenter.Models;
using CommandCenter.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
        private GameSettingsFile _settings = new();
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

                if (cmbAnticheatPlugin.Items.Count > 0)
                {
                    int selectIndex = 0;
                    for (int i = 0; i < cmbAnticheatPlugin.Items.Count; i++)
                    {
                        if (cmbAnticheatPlugin.Items[i] is ComboBoxItem item &&
                            item.Tag?.ToString() == _settings.plugins.anticheat)
                        {
                            selectIndex = i;
                            break;
                        }
                    }
                    cmbAnticheatPlugin.SelectedIndex = selectIndex;
                    DisplayAnticheatPluginDetails();
                }
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
            var tab = new[] { rbCamera, rbChat, rbInput, rbGraphics, rbSocial, rbNetwork, rbDataPacks, rbPlugins, rbLauncher }
                .FirstOrDefault(r => r.Name.Equals("rb" + part.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (tab != null)
                tab.IsChecked = true;
        }

        private void OptionsPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadGameSettings();
            LoadLauncherOptions();
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelCamera == null) return; // not yet loaded

            PanelCamera.Visibility = Visibility.Collapsed;
            PanelChat.Visibility = Visibility.Collapsed;
            PanelInput.Visibility = Visibility.Collapsed;
            PanelGraphics.Visibility = Visibility.Collapsed;
            PanelSocial.Visibility = Visibility.Collapsed;
            PanelNetwork.Visibility = Visibility.Collapsed;
            PanelDataPacks.Visibility = Visibility.Collapsed;
            PanelPlugins.Visibility = Visibility.Collapsed;
            PanelLauncher.Visibility = Visibility.Collapsed;

            if (ReferenceEquals(sender, rbCamera)) PanelCamera.Visibility = Visibility.Visible;
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

        private static string SettingsFilePath => GamePaths.GoSettings;

        private static string IniFilePath => GamePaths.Options;

        private static int ParseInt(TextBox tb, int defaultVal)
        {
            return int.TryParse(tb.Text, out int v) ? v : defaultVal;
        }

        public void LoadGameSettings()
        {
            try
            {
                // A missing file shows the defaults; Generals Online creates its own file, and this page writes one only on save
                _settings = File.Exists(SettingsFilePath)
                    ? System.Text.Json.JsonSerializer.Deserialize<GameSettingsFile>(File.ReadAllText(SettingsFilePath)) ?? new()
                    : new GameSettingsFile();

                txtMaxCameraHeight.Text = ((int)_settings.camera.max_height_only_when_lobby_host).ToString();
                txtMinCameraHeight.Text = ((int)_settings.camera.min_height).ToString();
                txtCameraMovementSpeed.Text = ((int)(_settings.camera.move_speed_ratio * 100f)).ToString();

                txtChatDuration.Text = _settings.chat.duration_seconds_until_fade_out.ToString();

                chkLimitFramerate.IsChecked = _settings.render.limit_framerate;
                txtFPSLimit.Text = _settings.render.fps_limit.ToString();
                txtFPSLimit.IsEnabled = _settings.render.limit_framerate;
                chkStatsOverlay.IsChecked = _settings.render.stats_overlay;

                var launcher = LauncherJson.Load();
                chkWindowed.IsChecked = launcher.Windowed;
                PopulateResolutions(launcher.WindowedWidth, launcher.WindowedHeight);
                cmbWindowedResolution.IsEnabled = launcher.Windowed;

                chkFriendOnlineInGame.IsChecked = _settings.social.notification_friend_comes_online_gameplay;
                chkFriendOnlineInMenus.IsChecked = _settings.social.notification_friend_comes_online_menus;
                chkFriendOfflineInGame.IsChecked = _settings.social.notification_friend_goes_offline_gameplay;
                chkFriendOfflineInMenus.IsChecked = _settings.social.notification_friend_goes_offline_menus;
                chkFriendRequestInGame.IsChecked = _settings.social.notification_player_sends_request_gameplay;
                chkFriendRequestInMenus.IsChecked = _settings.social.notification_player_sends_request_menus;
                chkFriendAcceptedInGame.IsChecked = _settings.social.notification_player_accepts_request_gameplay;
                chkFriendAcceptedInMenus.IsChecked = _settings.social.notification_player_accepts_request_menus;

                int httpVer = _settings.network.http_version;
                cmbHTTPVersion.SelectedIndex = httpVer < cmbHTTPVersion.Items.Count ? httpVer : 0;
                chkAlternativeEndpoint.IsChecked = _settings.network.use_alternative_endpoint;

                chkUseCommunityDataPatch.IsChecked = _settings.data_packs.use_community_data_patch;

                LoadIniSettings();
                
                // If anticheat was empty, default to first plugin and save
                if (string.IsNullOrEmpty(_settings.plugins.anticheat))
                {
                    DefaultAnticheatToFirstPlugin();
                }
            }
            catch
            {
                MessageBox.Show(Loc.T("Your settings have been reset due to an update. Please reconfigure them."),
                    "Command Center", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, Loc.MessageBoxOptions);
                Views.Main.ShowHome();
            }
        }
        
        private void DefaultAnticheatToFirstPlugin()
        {
            try
            {
                string pluginsPath = Path.Combine(GamePaths.Game, "plugins");
                
                if (!Directory.Exists(pluginsPath))
                    return;

                // Check if current anticheat's plugin still exists with valid DLL
                if (!string.IsNullOrEmpty(_settings.plugins.anticheat))
                {
                    string currentPluginPath = Path.Combine(pluginsPath, _settings.plugins.anticheat);
                    var dllFiles = Directory.GetFiles(currentPluginPath, "*.dll");
                    
                    // If plugin folder exists and has a DLL, keep it
                    if (Directory.Exists(currentPluginPath) && dllFiles.Any())
                        return;
                    
                    // Plugin or DLL is missing, will default to first below
                }

                var firstPlugin = Directory.GetDirectories(pluginsPath)
                    .Where(dir => Directory.GetFiles(dir, "*.json").Any() && Directory.GetFiles(dir, "*.dll").Any())
                    .OrderBy(dir => new DirectoryInfo(dir).Name)
                    .FirstOrDefault();

                if (firstPlugin != null)
                {
                    _settings.plugins.anticheat = new DirectoryInfo(firstPlugin).Name;
                }
            }
            catch
            {
                // silently fail if plugins folder doesn't exist or can't be read
            }
        }

        private void LoadIniSettings()
        {
            if (!File.Exists(IniFilePath))
            {
                WriteDefaultIni();
                return;
            }
            try
            {
                var dict = File.ReadLines(IniFilePath)
                    .Where(l => !string.IsNullOrWhiteSpace(l) && l.Contains(" = "))
                    .Select(l => l.Split(" = ", 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

                bool GetBool(string key) => dict.TryGetValue(key, out var v) && v.ToLower().Contains("yes");

                chkCursorLockFullscreen.IsChecked = GetBool("CursorCaptureEnabledInFullscreenGame") ||
                                                    GetBool("CursorCaptureEnabledInFullscreenMenu");
                chkCursorLockWindowed.IsChecked = GetBool("CursorCaptureEnabledInWindowedGame") ||
                                                  GetBool("CursorCaptureEnabledInWindowedMenu");
                chkEdgeScrollingFullscreen.IsChecked = GetBool("ScreenEdgeScrollEnabledInFullscreenApp");
                chkEdgeScrollingWindowed.IsChecked = GetBool("ScreenEdgeScrollEnabledInWindowedApp");
            }
            catch
            {
                WriteDefaultIni();
            }
        }

        private void WriteDefaultIni()
        {
            string[] lines =
            {
                "ScreenEdgeScrollEnabledInFullscreenApp = yes",
                "ScreenEdgeScrollEnabledInWindowedApp = no",
                "CursorCaptureEnabledInFullscreenGame = yes",
                "CursorCaptureEnabledInFullscreenMenu = yes",
                "CursorCaptureEnabledInWindowedGame = no",
                "CursorCaptureEnabledInWindowedMenu = no"
            };
            File.WriteAllLines(IniFilePath, lines);
            chkCursorLockWindowed.IsChecked = false;
            chkCursorLockFullscreen.IsChecked = true;
            chkEdgeScrollingFullscreen.IsChecked = true;
            chkEdgeScrollingWindowed.IsChecked = false;
        }

        private void SaveGameSettings()
        {
            _settings.camera.max_height_only_when_lobby_host = ParseInt(txtMaxCameraHeight, 310);
            _settings.camera.min_height = ParseInt(txtMinCameraHeight, 210);
            _settings.camera.move_speed_ratio = ParseInt(txtCameraMovementSpeed, 100) / 100f;

            _settings.chat.duration_seconds_until_fade_out = ParseInt(txtChatDuration, 30);

            _settings.render.limit_framerate = chkLimitFramerate.IsChecked == true;
            _settings.render.fps_limit = ParseInt(txtFPSLimit, 60);
            _settings.render.stats_overlay = chkStatsOverlay.IsChecked == true;

            SaveWindowedToLauncherJson();

            _settings.social.notification_friend_comes_online_gameplay = chkFriendOnlineInGame.IsChecked == true;
            _settings.social.notification_friend_comes_online_menus = chkFriendOnlineInMenus.IsChecked == true;
            _settings.social.notification_friend_goes_offline_gameplay = chkFriendOfflineInGame.IsChecked == true;
            _settings.social.notification_friend_goes_offline_menus = chkFriendOfflineInMenus.IsChecked == true;
            _settings.social.notification_player_sends_request_gameplay = chkFriendRequestInGame.IsChecked == true;
            _settings.social.notification_player_sends_request_menus = chkFriendRequestInMenus.IsChecked == true;
            _settings.social.notification_player_accepts_request_gameplay = chkFriendAcceptedInGame.IsChecked == true;
            _settings.social.notification_player_accepts_request_menus = chkFriendAcceptedInMenus.IsChecked == true;

            _settings.network.http_version = cmbHTTPVersion.SelectedIndex;
            _settings.network.use_alternative_endpoint = chkAlternativeEndpoint.IsChecked == true;

            bool patchChanged = _settings.data_packs.use_community_data_patch != (chkUseCommunityDataPatch.IsChecked == true);
            _settings.data_packs.use_community_data_patch = chkUseCommunityDataPatch.IsChecked == true;

            if (cmbAnticheatPlugin.SelectedItem is ComboBoxItem pluginItem)
                _settings.plugins.anticheat = pluginItem.Tag?.ToString() ?? "";

            SaveLauncherOptions();

            // The game reads settings.json and Options.ini when it starts and writes Options.ini itself
            if (GameLauncher.IsGameRunning())
            {
                if (GoSettingsChanged(GoSettings.Load()) || IniSettingsChanged())
                    Views.Main.Toast(Loc.T("Close the game first; it reads these settings only when it starts."), isError: true);
                return;
            }

            // settings.json is edited as a JSON tree, so fields added by newer Generals Online versions stay as they are
            var go = GoSettings.Load();
            if (GoSettingsChanged(go))
                go.Save("Saved from the Options page");
            SaveIniSettings();

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
            cmbLanguage.Items.Clear();
            foreach (var (code, name) in Loc.Languages)
            {
                var item = new ComboBoxItem { Content = name, Tag = code };
                cmbLanguage.Items.Add(item);
                if (code == Loc.Language)
                    cmbLanguage.SelectedItem = item;
            }
            txtMapLibrary.Text = AppSettings.Current.MapLibrary ?? "";
            txtVersion.Text = "Command Center " + AppState.Version;
            _loadingLauncher = false;
        }

        private void SaveLauncherOptions()
        {
            string source = txtMapLibrary.Text.Trim();
            string? value = source.Length == 0 ? null : source;
            if (value == AppSettings.Current.MapLibrary)
                return;
            AppSettings.Current.MapLibrary = value;
            AppSettings.Current.Save();
        }

        private void BtnMapLibraryDefault_Click(object sender, RoutedEventArgs e) => txtMapLibrary.Text = "";

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
            try
            {
                var launcher = LauncherJson.Load();
                launcher.Windowed = chkWindowed.IsChecked == true;
                if (cmbWindowedResolution.SelectedItem is ComboBoxItem item && item.Tag is (int w, int h))
                {
                    launcher.WindowedWidth = w;
                    launcher.WindowedHeight = h;
                }
                launcher.Save();
            }
            catch { }
        }

        // Puts the values from this page into the settings.json tree; true when that changes anything
        private bool GoSettingsChanged(GoSettings go)
        {
            string before = go.Root.ToJsonString();
            go.Set("camera", "max_height_only_when_lobby_host", _settings.camera.max_height_only_when_lobby_host);
            go.Set("camera", "min_height", _settings.camera.min_height);
            go.Set("camera", "move_speed_ratio", _settings.camera.move_speed_ratio);
            go.Set("chat", "duration_seconds_until_fade_out", _settings.chat.duration_seconds_until_fade_out);
            go.Set("render", "fps_limit", _settings.render.fps_limit);
            go.Set("render", "limit_framerate", _settings.render.limit_framerate);
            go.Set("render", "stats_overlay", _settings.render.stats_overlay);
            var social = _settings.social;
            go.Set("social", "notification_friend_comes_online_menus", social.notification_friend_comes_online_menus);
            go.Set("social", "notification_friend_comes_online_gameplay", social.notification_friend_comes_online_gameplay);
            go.Set("social", "notification_friend_goes_offline_menus", social.notification_friend_goes_offline_menus);
            go.Set("social", "notification_friend_goes_offline_gameplay", social.notification_friend_goes_offline_gameplay);
            go.Set("social", "notification_player_accepts_request_menus", social.notification_player_accepts_request_menus);
            go.Set("social", "notification_player_accepts_request_gameplay", social.notification_player_accepts_request_gameplay);
            go.Set("social", "notification_player_sends_request_menus", social.notification_player_sends_request_menus);
            go.Set("social", "notification_player_sends_request_gameplay", social.notification_player_sends_request_gameplay);
            go.Set("network", "http_version", _settings.network.http_version);
            go.Set("network", "use_alternative_endpoint", _settings.network.use_alternative_endpoint);
            go.Set("data_packs", "use_community_data_patch", _settings.data_packs.use_community_data_patch);
            go.Set("plugins", "anticheat", _settings.plugins.anticheat);
            return go.Root.ToJsonString() != before;
        }

        private void SaveIniSettings() => WriteIfChanged("Game options", IniFilePath, BuildIni());

        private bool IniSettingsChanged() => !(File.Exists(IniFilePath) && File.ReadAllBytes(IniFilePath).AsSpan().SequenceEqual(BuildIni()));

        // Options.ini with the edge scrolling and cursor lock choices; every other line stays as it is
        private byte[] BuildIni()
        {
            bool cursorWin = chkCursorLockWindowed.IsChecked == true;
            bool cursorFull = chkCursorLockFullscreen.IsChecked == true;
            bool edgeFull = chkEdgeScrollingFullscreen.IsChecked == true;
            bool edgeWin = chkEdgeScrollingWindowed.IsChecked == true;

            var lines = File.Exists(IniFilePath)
                ? File.ReadAllLines(IniFilePath).ToList()
                : new List<string>();

            bool wroteEdgeFull = false, wroteEdgeWin = false;
            bool wroteCursorFull = false, wroteCursorWin = false;

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains("ScreenEdgeScrollEnabledInFullscreenApp")) { lines[i] = $"ScreenEdgeScrollEnabledInFullscreenApp = {Y(edgeFull)}"; wroteEdgeFull = true; }
                else if (lines[i].Contains("ScreenEdgeScrollEnabledInWindowedApp")) { lines[i] = $"ScreenEdgeScrollEnabledInWindowedApp = {Y(edgeWin)}"; wroteEdgeWin = true; }
                else if (lines[i].Contains("CursorCaptureEnabledInFullscreenGame")) { lines[i] = $"CursorCaptureEnabledInFullscreenGame = {Y(cursorFull)}"; wroteCursorFull = true; }
                else if (lines[i].Contains("CursorCaptureEnabledInFullscreenMenu")) { lines[i] = $"CursorCaptureEnabledInFullscreenMenu = {Y(cursorFull)}"; wroteCursorFull = true; }
                else if (lines[i].Contains("CursorCaptureEnabledInWindowedGame")) { lines[i] = $"CursorCaptureEnabledInWindowedGame = {Y(cursorWin)}"; wroteCursorWin = true; }
                else if (lines[i].Contains("CursorCaptureEnabledInWindowedMenu")) { lines[i] = $"CursorCaptureEnabledInWindowedMenu = {Y(cursorWin)}"; wroteCursorWin = true; }
            }

            if (!wroteEdgeFull) lines.Add($"ScreenEdgeScrollEnabledInFullscreenApp = {Y(edgeFull)}");
            if (!wroteEdgeWin) lines.Add($"ScreenEdgeScrollEnabledInWindowedApp = {Y(edgeWin)}");
            if (!wroteCursorFull) { lines.Add($"CursorCaptureEnabledInFullscreenGame = {Y(cursorFull)}"); lines.Add($"CursorCaptureEnabledInFullscreenMenu = {Y(cursorFull)}"); }
            if (!wroteCursorWin) { lines.Add($"CursorCaptureEnabledInWindowedGame = {Y(cursorWin)}"); lines.Add($"CursorCaptureEnabledInWindowedMenu = {Y(cursorWin)}"); }

            return Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");
        }

        // Saving goes through the backup list, so the change can be undone; an unchanged file is left alone
        private static void WriteIfChanged(string title, string path, byte[] content)
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                return;
            BackupService.WriteFile(title, path, content, "Saved from the Options page");
        }

        private static string Y(bool b) => b ? "yes" : "no";

        // ─── Network Diagnostics ─────────────────────────────────────────────────

        private async void BtnRunDiagnostics_Click(object sender, RoutedEventArgs e)
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
            {
                string location = r.GeoCountryCode == "US" && r.GeoCity.Length > 0
                    ? $"{r.GeoCity}, {r.GeoCountry}" : r.GeoCountry;
                sb.AppendLine($"Location:     {location} - {r.GeoContinent}");
                sb.AppendLine($"ISP:          {r.GeoIsp}");
                sb.AppendLine($"External IP:  {RedactIp(r.GeoIp)}");
            }
            sb.AppendLine();
            sb.AppendLine("─ Internet ─");
            sb.AppendLine($"Cloudflare:   {(r.CloudflareSuccess ? $"{r.CloudflareAvgMs}ms avg, {r.CloudflareLossPercent}% loss" : "Unreachable")}");
            sb.AppendLine($"Download:     {(r.DownloadMbps > 0 ? $"{r.DownloadMbps:F1} Mbps" : "Failed")}");
            sb.AppendLine($"Upload:       {(r.UploadMbps > 0 ? $"{r.UploadMbps:F1} Mbps" : "Failed")}");
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
            foreach (var dot in new[] { dotGeo, dotCF, dotDl, dotUl, dotDns, dotPing, dotProto, dotHttp, dotServer, dotCdn, dotStun, dotTurn, dotNat })
                dot.Fill = gray;
            foreach (var val in new[] { valGeo, valCF, valDl, valUl, valDns, valPing, valProto, valHttp, valServer, valCdn, valStun, valTurn, valNat })
            {
                val.Text       = "checking…";
                val.Foreground = gray;
            }
        }

        private void UpdateDiagnosticsUI(NetworkDiagnosticsResult r)
        {
            // Geolocation
            if (r.GeoSuccess)
            {
                string location = r.GeoCountryCode == "US" && r.GeoCity.Length > 0
                    ? $"{r.GeoCity}, {r.GeoCountry}"
                    : r.GeoCountry;
                string geoText = location;
                if (r.GeoContinent.Length > 0) geoText += $" - {r.GeoContinent}";
                if (r.GeoIsp.Length > 0)       geoText += $" - {r.GeoIsp}";
                SetRow(dotGeo, valGeo, true, geoText, DiagGreen);
            }
            else
                SetRow(dotGeo, valGeo, false, Loc.T("Could not determine location"), DiagGray);

            // Cloudflare internet baseline
            SetRow(dotCF, valCF,
                r.CloudflareSuccess,
                r.CloudflareSuccess
                    ? Loc.T("{0} ms avg  {1}% loss", r.CloudflareAvgMs, r.CloudflareLossPercent)
                    : Loc.T("Unreachable"),
                r.CloudflareSuccess ? LossColor(r.CloudflareLossPercent) : DiagRed);

            // Download speed
            SetRow(dotDl, valDl,
                r.SpeedTestSuccess && r.DownloadMbps > 0,
                r.DownloadMbps > 0 ? $"{r.DownloadMbps:F1} Mbps" : Loc.T("Failed"),
                r.DownloadMbps > 0 ? SpeedColor(r.DownloadMbps) : DiagRed);

            // Upload speed
            SetRow(dotUl, valUl,
                r.SpeedTestSuccess && r.UploadMbps > 0,
                r.UploadMbps > 0 ? $"{r.UploadMbps:F1} Mbps" : Loc.T("Failed"),
                r.UploadMbps > 0 ? SpeedColor(r.UploadMbps) : DiagRed);

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
            var (natColor, natText) = r.NatType switch
            {
                NatType.Open or NatType.FullCone or NatType.RestrictedCone
                    => (DiagGreen,  $"{NatTypeLabel(r.NatType)} — {r.NatTypeDetail}"),
                NatType.PortRestrictedCone
                    => (DiagYellow, Loc.T("Port Restricted Cone") + $" — {r.NatTypeDetail}"),
                NatType.Symmetric
                    => (DiagRed,    Loc.T("Symmetric") + $" — {r.NatTypeDetail}"),
                _   => (DiagGray,   Loc.T("Unknown") + $" — {r.NatTypeDetail}")
            };
            SetRow(dotNat, valNat, r.NatType != NatType.Unknown, natText, natColor);
        }

        private void AppendDiagLog(NetworkDiagnosticsResult r)
        {
            const int MaxEntries = 10;

            string entry = $"[{r.Timestamp:HH:mm:ss}]  " +
                $"↓ {(r.DownloadMbps > 0 ? $"{r.DownloadMbps:F1}Mbps" : "✗")}  " +
                $"↑ {(r.UploadMbps > 0 ? $"{r.UploadMbps:F1}Mbps" : "✗")}  |  " +
                $"CF: {(r.CloudflareSuccess ? $"{r.CloudflareAvgMs}ms" : "✗")}  |  " +
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
            val.Text       = text;
            val.Foreground = color;
        }

        private static SolidColorBrush LatencyColor(int ms) =>
            ms <= 60 ? DiagGreen : ms <= 150 ? DiagYellow : DiagRed;

        private static SolidColorBrush LossColor(int pct) =>
            pct == 0 ? DiagGreen : pct < 25 ? DiagYellow : DiagRed;

        private static SolidColorBrush SpeedColor(double mbps) =>
            mbps >= 10 ? DiagGreen : mbps >= 1 ? DiagYellow : DiagRed;

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
