using System.Text;

namespace CommandCenter.Tests
{
    public class GoProfileTests
    {
        private static string RepoFile(string name)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, name);
                if (File.Exists(path))
                    return path;
            }
            throw new FileNotFoundException(name);
        }

        private const string Valid = """
            { "schema": 1, "liveExe": "GeneralsOnlineZH_60.exe", "testExe": "GeneralsOnlineZH_TestEnvironment.exe",
              "eacLauncherExe": "EAC_LaunchGeneralsOnline.exe", "officialLauncherExe": "GeneralsOnlineZH.exe",
              "eacPlugin": "easyanticheat", "channelKey": "prefer_experiemental_client",
              "communityPatchFile": "500_900_CommunityPatch_CoreINI.big",
              "apiBase": "https://api.playgenerals.online/env/prod/contract/1/", "manifestKey": "execrc_60" }
            """;

        [Fact]
        public void The_published_profile_is_accepted()
        {
            var profile = GoProfile.Parse(File.ReadAllText(RepoFile("go-profile.json")));
            Assert.NotNull(profile);
        }

        [Fact]
        public void A_valid_profile_is_read()
        {
            var profile = GoProfile.Parse(Valid);
            Assert.NotNull(profile);
            Assert.Equal("GeneralsOnlineZH_60.exe", profile!.LiveExe);
            Assert.Equal("500_900_CommunityPatch_CoreINI.big", profile.CommunityPatchFile);
        }

        [Theory]
        [InlineData("\"liveExe\": \"GeneralsOnlineZH_60.exe\"", "\"liveExe\": \"..\\\\..\\\\Windows\\\\notepad.exe\"")]
        [InlineData("\"liveExe\": \"GeneralsOnlineZH_60.exe\"", "\"liveExe\": \"C:/evil.exe\"")]
        [InlineData("\"liveExe\": \"GeneralsOnlineZH_60.exe\"", "\"liveExe\": \"setup.bat\"")]
        [InlineData("https://api.playgenerals.online/", "https://api.playgenerals.online.example.com/")]
        [InlineData("https://api.playgenerals.online/", "http://api.playgenerals.online/")]
        [InlineData("https://api.playgenerals.online/env/prod/contract/1/", "https://api.playgenerals.online/env/prod/contract/1")]
        [InlineData("\"schema\": 1", "\"schema\": 2")]
        [InlineData("500_900_CommunityPatch_CoreINI.big", "../../patch.big")]
        [InlineData("\"eacPlugin\": \"easyanticheat\"", "\"eacPlugin\": \"easy anticheat;rm\"")]
        public void A_profile_cannot_point_elsewhere(string part, string replacement) =>
            Assert.Null(GoProfile.Parse(Valid.Replace(part, replacement)));

        [Fact]
        public void A_broken_profile_is_ignored() => Assert.Null(GoProfile.Parse("{ not json"));
    }

    public class GoSettingsTests
    {
        private const string Json = """
            { "plugins": { "anticheat": "goanticheat" },
              "camera": { "max_height_only_when_lobby_host": "not a number" },
              "from_a_newer_version": { "list": [1, 2, 3] } }
            """;

        [Fact]
        public void Values_are_read() => Assert.Equal("goanticheat", GoSettings.Parse(Json).AntiCheat);

        [Fact]
        public void A_value_of_another_type_reads_as_the_default() =>
            Assert.Equal(310.0, GoSettings.Parse(Json).Get("camera", "max_height_only_when_lobby_host", 310.0));

        [Fact]
        public void Saving_keeps_fields_Command_Center_does_not_know()
        {
            var go = GoSettings.Parse(Json);
            go.Set("data_packs", "use_community_data_patch", true);
            var again = GoSettings.Parse(Encoding.UTF8.GetString(go.ToBytes()));
            Assert.True(again.Get("data_packs", "use_community_data_patch", false));
            Assert.Equal("[1,2,3]", again.Root["from_a_newer_version"]!["list"]!.ToJsonString());
            Assert.Equal("goanticheat", again.AntiCheat);
        }

        [Fact]
        public void A_broken_file_gives_empty_settings() => Assert.Empty(GoSettings.Parse("{ \"plugins\": ").Root);
    }
}
