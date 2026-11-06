using System.Text.Json.Nodes;
using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Settings, part 2 (ticket 09): Generals Online's sections on a view model. These tests pin what the Options page
    // read and wrote before the move: settings.json, launcher.json and Options.ini, only what changed, unknown fields
    // kept, and nothing written while the game runs.
    public sealed class GameSettingsViewModelTests : IDisposable
    {
        private readonly TempGame _game = new();
        private readonly FakeShell _shell = new();
        private bool _running;
        private int _patchChanges;

        private static readonly (int, int)[] Modes = [(1280, 720), (1600, 900), (1920, 1080)];

        private GameSettingsViewModel Model()
        {
            var model = new GameSettingsViewModel(_shell, () => _running, () => Modes, () => _patchChanges++);
            model.Load();
            return model;
        }

        private void Settings(string json) => _game.Write(@"user\GeneralsOnlineData\settings.json", json);
        private void Launcher(string json) => _game.Write(@"user\GeneralsOnlineData\launcher.json", json);
        private void Options(string text) => _game.Write(@"user\Options.ini", text);

        private static JsonNode Saved(string file) => JsonNode.Parse(File.ReadAllText(file))!;

        [Fact]
        public void Values_are_read_from_the_settings_files()
        {
            Settings("""
                {"camera":{"max_height_only_when_lobby_host":400,"min_height":250,"move_speed_ratio":1.5},
                 "chat":{"duration_seconds_until_fade_out":45},
                 "render":{"limit_framerate":false,"fps_limit":144,"stats_overlay":false},
                 "social":{"notification_friend_comes_online_gameplay":false,"notification_player_accepts_request_menus":false},
                 "network":{"http_version":2,"use_alternative_endpoint":true},
                 "data_packs":{"use_community_data_patch":false}}
                """);
            Launcher("""{"windowed":true,"windowed_width":1600,"windowed_height":900}""");
            Options("ScreenEdgeScrollEnabledInWindowedApp = yes\nCursorCaptureEnabledInFullscreenGame = no\nCursorCaptureEnabledInFullscreenMenu = no\n");

            var model = Model();
            Assert.Equal(("400", "250", "150"), (model.MaxCameraHeight, model.MinCameraHeight, model.CameraSpeed));
            Assert.Equal("45", model.ChatDuration);
            Assert.Equal((false, "144", false), (model.LimitFramerate, model.FpsLimit, model.StatsOverlay));
            Assert.False(model.FriendOnlineInGame);
            Assert.False(model.AcceptedInMenus);
            Assert.True(model.FriendOnlineInMenus);
            Assert.Equal((2, true), (model.HttpVersion, model.AlternativeEndpoint));
            Assert.False(model.CommunityPatch);
            Assert.Equal((true, "1600x900"), (model.Windowed, model.Resolution));
            Assert.Equal((false, true, true, true), (model.CursorLockFullscreen, model.CursorLockWindowed, model.EdgeScrollFullscreen, model.EdgeScrollWindowed));
        }

        [Fact]
        public void Missing_values_show_the_games_defaults()
        {
            var model = Model();
            Assert.Equal(("310", "210", "100", "30"), (model.MaxCameraHeight, model.MinCameraHeight, model.CameraSpeed, model.ChatDuration));
            Assert.Equal((true, "60", true), (model.LimitFramerate, model.FpsLimit, model.StatsOverlay));
            Assert.Equal((0, false, true), (model.HttpVersion, model.AlternativeEndpoint, model.CommunityPatch));
            Assert.Equal((false, "1920x1080"), (model.Windowed, model.Resolution));
            Assert.Equal((true, true, true, false), (model.CursorLockFullscreen, model.CursorLockWindowed, model.EdgeScrollFullscreen, model.EdgeScrollWindowed));
        }

        [Fact]
        public void Only_what_was_changed_is_written_and_fields_it_does_not_know_stay()
        {
            Settings("""{"camera":{"max_height_only_when_lobby_host":310.5,"future":1},"newer":{"x":true}}""");
            var model = Model();
            model.ChatDuration = "50";
            model.Commit();

            var saved = Saved(GamePaths.GoSettings);
            Assert.Equal(50, saved["chat"]!["duration_seconds_until_fade_out"]!.GetValue<int>());
            Assert.Equal(310.5, saved["camera"]!["max_height_only_when_lobby_host"]!.GetValue<double>());
            Assert.Equal(1, saved["camera"]!["future"]!.GetValue<int>());
            Assert.True(saved["newer"]!["x"]!.GetValue<bool>());
            Assert.False(File.Exists(GamePaths.Options));
            Assert.False(File.Exists(GamePaths.LauncherSettings));
        }

        [Fact]
        public void Nothing_is_written_while_the_game_runs_and_the_change_waits()
        {
            Settings("""{"chat":{"duration_seconds_until_fade_out":30}}""");
            var model = Model();
            model.ChatDuration = "50";
            _running = true;
            model.Commit();

            Assert.Equal(("Close the game first; it reads these settings only when it starts.", true), (_shell.Toasts[^1].Message, _shell.Toasts[^1].IsError));
            Assert.Equal(30, Saved(GamePaths.GoSettings)["chat"]!["duration_seconds_until_fade_out"]!.GetValue<int>());

            _running = false;
            model.Commit();
            Assert.Equal(50, Saved(GamePaths.GoSettings)["chat"]!["duration_seconds_until_fade_out"]!.GetValue<int>());
        }

        [Fact]
        public void Edge_scrolling_and_cursor_lock_write_Options_ini_one_box_for_the_game_and_the_menus()
        {
            Options("CursorCaptureEnabledInWindowedGame = yes\nCursorCaptureEnabledInWindowedMenu = no\n");
            var model = Model();
            model.CursorLockWindowed = false;
            model.EdgeScrollWindowed = true;
            model.Commit();

            var ini = OptionsFile.Read();
            Assert.Equal(("no", "no", "yes"), (ini["CursorCaptureEnabledInWindowedGame"], ini["CursorCaptureEnabledInWindowedMenu"], ini["ScreenEdgeScrollEnabledInWindowedApp"]));
        }

        [Fact]
        public void Windowed_mode_and_its_size_go_to_launcher_json_with_its_other_fields_kept()
        {
            Launcher("""{"prefer_experiemental_client":false,"windowed":false}""");
            var model = Model();
            model.Windowed = true;
            model.Resolution = "1600x900";
            model.Commit();

            var saved = Saved(GamePaths.LauncherSettings);
            Assert.True(saved["windowed"]!.GetValue<bool>());
            Assert.Equal((1600, 900), (saved["windowed_width"]!.GetValue<int>(), saved["windowed_height"]!.GetValue<int>()));
            Assert.False(saved["prefer_experiemental_client"]!.GetValue<bool>());
        }

        [Fact]
        public void A_window_size_the_screen_does_not_list_stays_when_only_windowed_mode_changes()
        {
            Launcher("""{"windowed":false,"windowed_width":1366,"windowed_height":768}""");
            var model = Model();
            model.Windowed = true;
            model.Commit();

            var saved = Saved(GamePaths.LauncherSettings);
            Assert.True(saved["windowed"]!.GetValue<bool>());
            Assert.Equal((1366, 768), (saved["windowed_width"]!.GetValue<int>(), saved["windowed_height"]!.GetValue<int>()));
        }

        [Fact]
        public void Window_sizes_are_the_screens_modes_from_640_by_480_largest_first()
        {
            var model = new GameSettingsViewModel(_shell, () => false, () => [(800, 600), (1920, 1080), (320, 200), (1280, 720), (1920, 1080), (1920, 1200)], () => { });
            Assert.Equal(["1920x1200", "1920x1080", "1280x720", "800x600"], model.Resolutions.Select(r => r.Value));
            Assert.Equal("1920 × 1080", model.Resolutions[1].Label);
        }

        [Fact]
        public void Turning_the_community_patch_off_or_on_refreshes_the_hotkey_letters()
        {
            var model = Model();
            model.CommunityPatch = false;
            model.Commit();
            Assert.Equal(1, _patchChanges);
            Assert.False(Saved(GamePaths.GoSettings)["data_packs"]!["use_community_data_patch"]!.GetValue<bool>());
        }

        [Fact]
        public void The_anti_cheat_is_written_only_when_the_player_picks_one()
        {
            Settings("""{"plugins":{"anticheat":"easyanticheat"},"chat":{"duration_seconds_until_fade_out":30}}""");
            var model = Model();
            model.ChatDuration = "40";
            model.Commit();
            Assert.Equal("easyanticheat", Saved(GamePaths.GoSettings)["plugins"]!["anticheat"]!.GetValue<string>());

            model.ChooseAntiCheat("generalsonline");
            model.Commit();
            Assert.Equal("generalsonline", Saved(GamePaths.GoSettings)["plugins"]!["anticheat"]!.GetValue<string>());
        }

        public void Dispose() => _game.Dispose();
    }
}
