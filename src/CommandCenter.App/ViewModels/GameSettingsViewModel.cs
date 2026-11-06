using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CommandCenter.ViewModels
{
    // A Settings section that reads its files when Settings opens and writes what changed when Settings is left
    // (ticket 09: one load and commit contract for the page)
    public interface ISettingsSection
    {
        void Load();
        void Commit();
    }

    // Settings, part 2 (ticket 09): Generals Online's sections, camera, chat, input, graphics, social, network, data packs
    // and the anti-cheat choice. They read settings.json, launcher.json and Options.ini; Commit writes back only what the
    // player changed, each file edited in place so fields Command Center does not know stay. Moved from the Options page's
    // code-behind with its rules unchanged.
    public sealed partial class GameSettingsViewModel : ObservableObject, ISettingsSection
    {
        private readonly IShell _shell;
        private readonly Func<bool> _gameRunning;
        private readonly Action _patchChanged;

        // What each value showed after loading; only values the player changed are written back, so values the page
        // cannot show exactly (decimals, separate game and menu flags) stay as they are
        private Dictionary<string, string> _shown = new();
        private bool _loaded;
        private string _antiCheat = "";
        private string? _antiCheatChosen;

        public GameSettingsViewModel(IShell shell, Func<bool> gameRunning, Func<IReadOnlyList<(int Width, int Height)>> resolutions,
            Action patchChanged)
        {
            _shell = shell;
            _gameRunning = gameRunning;
            _patchChanged = patchChanged;
            // The screen's modes from 640 × 480 up, largest first
            Resolutions = resolutions()
                .Where(r => r.Width >= 640 && r.Height >= 480)
                .Distinct()
                .OrderByDescending(r => r.Width).ThenByDescending(r => r.Height)
                .Select(r => new ResolutionOption($"{r.Width}x{r.Height}", $"{r.Width} × {r.Height}"))
                .ToList();
            HttpVersions = new[] { "Automatic", "1.0", "1.1", "2.0", "3.0" }
                .Select((label, index) => new Choice<int>(index, label, () => HttpVersion, chosen => HttpVersion = chosen))
                .ToList();
        }

        // Camera
        [ObservableProperty] private string _maxCameraHeight = "";
        [ObservableProperty] private string _minCameraHeight = "";
        [ObservableProperty] private string _cameraSpeed = "";

        // Chat
        [ObservableProperty] private string _chatDuration = "";

        // Graphics
        [ObservableProperty] private bool _limitFramerate;
        [ObservableProperty] private string _fpsLimit = "";
        [ObservableProperty] private bool _statsOverlay;
        [ObservableProperty] private bool _windowed;
        [ObservableProperty] private string? _resolution;
        public IReadOnlyList<ResolutionOption> Resolutions { get; }

        // Social: each notification in the game and in the menus
        [ObservableProperty] private bool _friendOnlineInGame;
        [ObservableProperty] private bool _friendOnlineInMenus;
        [ObservableProperty] private bool _friendOfflineInGame;
        [ObservableProperty] private bool _friendOfflineInMenus;
        [ObservableProperty] private bool _requestInGame;
        [ObservableProperty] private bool _requestInMenus;
        [ObservableProperty] private bool _acceptedInGame;
        [ObservableProperty] private bool _acceptedInMenus;

        // Input (Options.ini): one box sets both the game and the menu key
        [ObservableProperty] private bool _cursorLockFullscreen;
        [ObservableProperty] private bool _cursorLockWindowed;
        [ObservableProperty] private bool _edgeScrollFullscreen;
        [ObservableProperty] private bool _edgeScrollWindowed;

        // Network
        [ObservableProperty] private int _httpVersion;
        [ObservableProperty] private bool _alternativeEndpoint;
        public IReadOnlyList<Choice<int>> HttpVersions { get; }

        // Data packs
        [ObservableProperty] private bool _communityPatch;

        // The anti-cheat plugin settings.json names, or the one the player picked in Plugins
        public string AntiCheat => _antiCheatChosen ?? _antiCheat;

        partial void OnHttpVersionChanged(int value)
        {
            foreach (var choice in HttpVersions)
                choice.Refresh();
        }

        public void ChooseAntiCheat(string plugin)
        {
            _antiCheatChosen = plugin;
            OnPropertyChanged(nameof(AntiCheat));
        }

        // Reads the three files. A value that is missing or of an unexpected type shows its default; nothing is written.
        public void Load()
        {
            try
            {
                var go = GoSettings.Load();
                MaxCameraHeight = Whole(go.Get("camera", "max_height_only_when_lobby_host", 310.0));
                MinCameraHeight = Whole(go.Get("camera", "min_height", 210.0));
                CameraSpeed = Whole(go.Get("camera", "move_speed_ratio", 1.0) * 100);
                ChatDuration = Whole(go.Get("chat", "duration_seconds_until_fade_out", 30.0));
                LimitFramerate = go.Get("render", "limit_framerate", true);
                FpsLimit = Whole(go.Get("render", "fps_limit", 60.0));
                StatsOverlay = go.Get("render", "stats_overlay", true);

                var launcher = LauncherJson.Load();
                Windowed = launcher.Windowed;
                string wanted = $"{launcher.WindowedWidth}x{launcher.WindowedHeight}";
                Resolution = Resolutions.Any(r => r.Value == wanted) ? wanted : Resolutions.FirstOrDefault()?.Value;

                FriendOnlineInGame = go.Get("social", "notification_friend_comes_online_gameplay", true);
                FriendOnlineInMenus = go.Get("social", "notification_friend_comes_online_menus", true);
                FriendOfflineInGame = go.Get("social", "notification_friend_goes_offline_gameplay", true);
                FriendOfflineInMenus = go.Get("social", "notification_friend_goes_offline_menus", true);
                RequestInGame = go.Get("social", "notification_player_sends_request_gameplay", true);
                RequestInMenus = go.Get("social", "notification_player_sends_request_menus", true);
                AcceptedInGame = go.Get("social", "notification_player_accepts_request_gameplay", true);
                AcceptedInMenus = go.Get("social", "notification_player_accepts_request_menus", true);

                int http = (int)go.Get("network", "http_version", 0.0);
                HttpVersion = http >= 0 && http < HttpVersions.Count ? http : 0;
                AlternativeEndpoint = go.Get("network", "use_alternative_endpoint", false);
                CommunityPatch = go.Get("data_packs", "use_community_data_patch", true);

                _antiCheat = go.AntiCheat;
                _antiCheatChosen = null;
                OnPropertyChanged(nameof(AntiCheat));

                // The game's defaults for keys Options.ini does not have yet
                var ini = OptionsFile.Read();
                bool Yes(string key, bool fallback) => ini.TryGetValue(key, out var v) ? v.Equals("yes", StringComparison.OrdinalIgnoreCase) : fallback;
                CursorLockFullscreen = Yes("CursorCaptureEnabledInFullscreenGame", true) || Yes("CursorCaptureEnabledInFullscreenMenu", true);
                CursorLockWindowed = Yes("CursorCaptureEnabledInWindowedGame", true) || Yes("CursorCaptureEnabledInWindowedMenu", false);
                EdgeScrollFullscreen = Yes("ScreenEdgeScrollEnabledInFullscreenApp", true);
                EdgeScrollWindowed = Yes("ScreenEdgeScrollEnabledInWindowedApp", false);

                _shown = Shown();
                _loaded = true;
            }
            catch (Exception ex)
            {
                // Saving stays off, so nothing is written from values that were not read
                _loaded = false;
                _shell.Toast(string.Format("Some settings could not be read, so this page will not save anything: {0}", ex.Message), isError: true);
            }
        }

        // Writes only what the player changed since loading. The game reads settings.json and Options.ini when it starts
        // and writes Options.ini itself, so while it runs nothing is written and the changes wait for the next time.
        public void Commit()
        {
            if (!_loaded)
                return;

            var now = Shown();
            bool patchChanged = Changed(now, "data_packs/use_community_data_patch");
            var go = GoSettings.Load();
            bool goChanged = ApplyGoChanges(go, now);
            var ini = IniChanges(now);
            bool iniChanged = OptionsFile.WouldChange(ini);
            bool launcherChanged = Changed(now, "launcher/windowed") || Changed(now, "launcher/resolution");

            if ((goChanged || iniChanged) && _gameRunning())
            {
                _shell.Toast("Close the game first; it reads these settings only when it starts.", isError: true);
                return;
            }

            try
            {
                if (goChanged)
                    go.Save("Saved from Settings");
                if (iniChanged)
                    OptionsFile.Write(ini, "Game options", "Saved from Settings");
                if (launcherChanged)
                    SaveLauncher();
            }
            catch (Exception ex)
            {
                _shell.Toast(string.Format("Could not save: {0}", ex.Message), isError: true);
                return;
            }
            _shown = now;
            if (goChanged)
                _antiCheat = go.AntiCheat;
            _antiCheatChosen = null;
            // The hotkey letters on the pictures follow the buttons the game will show
            if (patchChanged)
                _patchChanged();
        }

        private static string Whole(double value) => ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        private static string Flag(bool value) => value ? "1" : "0";
        private static int Number(string text, int fallback) => int.TryParse(text, out int value) ? value : fallback;
        private static string Yes(bool value) => value ? "yes" : "no";

        // settings.json fields: the text each shows, and the value it stands for
        private (string Section, string Key, string Shown, Func<JsonNode?> Value)[] Fields() =>
        [
            ("camera", "max_height_only_when_lobby_host", MaxCameraHeight, () => JsonValue.Create(Number(MaxCameraHeight, 310))),
            ("camera", "min_height", MinCameraHeight, () => JsonValue.Create(Number(MinCameraHeight, 210))),
            ("camera", "move_speed_ratio", CameraSpeed, () => JsonValue.Create(Number(CameraSpeed, 100) / 100.0)),
            ("chat", "duration_seconds_until_fade_out", ChatDuration, () => JsonValue.Create(Number(ChatDuration, 30))),
            ("render", "limit_framerate", Flag(LimitFramerate), () => JsonValue.Create(LimitFramerate)),
            ("render", "fps_limit", FpsLimit, () => JsonValue.Create(Number(FpsLimit, 60))),
            ("render", "stats_overlay", Flag(StatsOverlay), () => JsonValue.Create(StatsOverlay)),
            ("social", "notification_friend_comes_online_gameplay", Flag(FriendOnlineInGame), () => JsonValue.Create(FriendOnlineInGame)),
            ("social", "notification_friend_comes_online_menus", Flag(FriendOnlineInMenus), () => JsonValue.Create(FriendOnlineInMenus)),
            ("social", "notification_friend_goes_offline_gameplay", Flag(FriendOfflineInGame), () => JsonValue.Create(FriendOfflineInGame)),
            ("social", "notification_friend_goes_offline_menus", Flag(FriendOfflineInMenus), () => JsonValue.Create(FriendOfflineInMenus)),
            ("social", "notification_player_sends_request_gameplay", Flag(RequestInGame), () => JsonValue.Create(RequestInGame)),
            ("social", "notification_player_sends_request_menus", Flag(RequestInMenus), () => JsonValue.Create(RequestInMenus)),
            ("social", "notification_player_accepts_request_gameplay", Flag(AcceptedInGame), () => JsonValue.Create(AcceptedInGame)),
            ("social", "notification_player_accepts_request_menus", Flag(AcceptedInMenus), () => JsonValue.Create(AcceptedInMenus)),
            ("network", "http_version", HttpVersion.ToString(CultureInfo.InvariantCulture), () => JsonValue.Create(HttpVersion)),
            ("network", "use_alternative_endpoint", Flag(AlternativeEndpoint), () => JsonValue.Create(AlternativeEndpoint)),
            ("data_packs", "use_community_data_patch", Flag(CommunityPatch), () => JsonValue.Create(CommunityPatch)),
        ];

        private Dictionary<string, string> Shown()
        {
            var shown = Fields().ToDictionary(f => f.Section + "/" + f.Key, f => f.Shown);
            shown["launcher/windowed"] = Flag(Windowed);
            shown["launcher/resolution"] = Resolution ?? "";
            shown["ini/cursorFull"] = Flag(CursorLockFullscreen);
            shown["ini/cursorWin"] = Flag(CursorLockWindowed);
            shown["ini/edgeFull"] = Flag(EdgeScrollFullscreen);
            shown["ini/edgeWin"] = Flag(EdgeScrollWindowed);
            return shown;
        }

        private bool Changed(Dictionary<string, string> now, string key) =>
            _shown.TryGetValue(key, out var was) && now.TryGetValue(key, out var value) && was != value;

        // Puts the values the player changed into the settings.json tree; true when that changes anything
        private bool ApplyGoChanges(GoSettings go, Dictionary<string, string> now)
        {
            string before = go.Root.ToJsonString();
            foreach (var field in Fields())
            {
                if (Changed(now, field.Section + "/" + field.Key))
                    go.SetNode(field.Section, field.Key, field.Value());
            }
            if (_antiCheatChosen is { Length: > 0 } plugin)
                go.Set("plugins", "anticheat", plugin);
            return go.Root.ToJsonString() != before;
        }

        private Dictionary<string, string> IniChanges(Dictionary<string, string> now)
        {
            var changes = new Dictionary<string, string>();
            if (Changed(now, "ini/edgeFull"))
                changes["ScreenEdgeScrollEnabledInFullscreenApp"] = Yes(EdgeScrollFullscreen);
            if (Changed(now, "ini/edgeWin"))
                changes["ScreenEdgeScrollEnabledInWindowedApp"] = Yes(EdgeScrollWindowed);
            if (Changed(now, "ini/cursorFull"))
            {
                changes["CursorCaptureEnabledInFullscreenGame"] = Yes(CursorLockFullscreen);
                changes["CursorCaptureEnabledInFullscreenMenu"] = Yes(CursorLockFullscreen);
            }
            if (Changed(now, "ini/cursorWin"))
            {
                changes["CursorCaptureEnabledInWindowedGame"] = Yes(CursorLockWindowed);
                changes["CursorCaptureEnabledInWindowedMenu"] = Yes(CursorLockWindowed);
            }
            return changes;
        }

        // launcher.json is edited as a JSON tree, so the client choice and anything else in it stays as it was
        private void SaveLauncher()
        {
            var launcher = LauncherJson.Load();
            launcher.Windowed = Windowed;
            if (Resolution?.Split('x') is [var width, var height] && int.TryParse(width, out int w) && int.TryParse(height, out int h))
            {
                launcher.WindowedWidth = w;
                launcher.WindowedHeight = h;
            }
            launcher.Save("Saved from Settings");
        }
    }
}
