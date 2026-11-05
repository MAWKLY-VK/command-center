using System.Windows.Input;
using CommandCenter.Input;

namespace CommandCenter.Tests
{
    // Pins the behaviour of the pieces that leave WPF and the App in phase 1 (ticket 03)

    public sealed class MapLibrarySourceTests : IDisposable
    {
        [Fact]
        public void The_map_library_is_the_override_then_the_library_folder_then_the_online_one()
        {
            Assert.Equal(MapLibraryClient.OnlineLibrary, MapLibraryClient.CurrentSource);
            MapLibraryClient.LibraryFolder = @"C:\Maps\Library";
            Assert.Equal(@"C:\Maps\Library", MapLibraryClient.CurrentSource);
            MapLibraryClient.SourceOverride = "  https://example.org/library/  ";
            Assert.Equal("https://example.org/library/", MapLibraryClient.CurrentSource);
            MapLibraryClient.SourceOverride = "   ";
            Assert.Equal(@"C:\Maps\Library", MapLibraryClient.CurrentSource);
        }

        public void Dispose()
        {
            MapLibraryClient.SourceOverride = null;
            MapLibraryClient.LibraryFolder = null;
        }
    }

    public class GameKeyNameTests
    {
        [Theory]
        [InlineData(Key.A, "KEY_A")]
        [InlineData(Key.D5, "KEY_5")]
        [InlineData(Key.F12, "KEY_F12")]
        [InlineData(Key.NumPad3, "KEY_KP3")]
        [InlineData(Key.OemQuestion, "KEY_SLASH")]
        [InlineData(Key.Space, "KEY_SPACE")]
        [InlineData(Key.LeftCtrl, null)]
        public void A_keyboard_key_maps_to_the_games_key_name(Key key, string? name) => Assert.Equal(name, GameKeyNames.Of(key));
    }

    // AppServices is where the run options reach the services at startup
    public sealed class AppServicesTests : IDisposable
    {
        private const string Profile = """
            {
              "schema": 1,
              "liveExe": "GeneralsOnlineZH_61.exe",
              "testExe": "GeneralsOnlineZH_TestEnvironment.exe",
              "eacLauncherExe": "EAC_LaunchGeneralsOnline.exe",
              "officialLauncherExe": "GeneralsOnlineZH.exe",
              "eacPlugin": "easyanticheat",
              "liveThroughEac": false,
              "testThroughEac": true,
              "channelKey": "prefer_experiemental_client",
              "communityPatchFile": "500_900_CommunityPatch_CoreINI.big",
              "apiBase": "https://api.playgenerals.online/env/prod/contract/1/",
              "manifestKey": "execrc_61"
            }
            """;

        private readonly TempGame _game = new();
        private string _root => _game.Root;

        [Fact]
        public void A_go_profile_file_given_at_startup_is_used_instead_of_the_cached_one()
        {
            string file = Path.Combine(_root, "profile.json");
            File.WriteAllText(file, Profile);
            TestKeys.SignFile(file, SignedKind.GoProfile);
            using var trust = TestKeys.Trust();
            AppServices.Configure(["CommandCenter.exe", "--go-profile", file]);
            GoProfile.Reset();
            Assert.Equal("GeneralsOnlineZH_61.exe", GoProfile.Current.LiveExe);
            Assert.Equal("execrc_61", GoProfile.Current.ManifestKey);
        }

        [Fact]
        public void The_map_library_options_reach_the_library_client()
        {
            AppServices.Configure(["CommandCenter.exe", "--library-folder", @"C:\Maps\Library"]);
            Assert.Equal(@"C:\Maps\Library", MapLibraryClient.CurrentSource);
            AppServices.Configure(["CommandCenter.exe", "--map-library", "https://example.org/library/", "--library-folder", @"C:\Maps\Library"]);
            Assert.Equal("https://example.org/library/", MapLibraryClient.CurrentSource);
        }

        [Fact]
        public void Option_names_are_not_case_sensitive()
        {
            AppServices.Configure(["CommandCenter.exe", "--SAMPLES", "--Library-Folder", @"C:\Maps\Library"]);
            Assert.True(AppState.Samples);
            Assert.Equal(@"C:\Maps\Library", MapLibraryClient.CurrentSource);
        }

        [Fact]
        public void Without_options_nothing_is_overridden()
        {
            AppServices.Configure(["CommandCenter.exe"]);
            Assert.Null(GoProfile.FileOverride);
            Assert.Equal(MapLibraryClient.OnlineLibrary, MapLibraryClient.CurrentSource);
            Assert.False(AppState.Samples);
        }

        public void Dispose()
        {
            AppServices.Configure(["CommandCenter.exe"]);
            GoProfile.Reset();
            _game.Dispose();
        }
    }
}
