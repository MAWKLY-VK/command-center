namespace CommandCenter.Tests
{
    // Home (ticket 13): which program Play starts and with which arguments, as the official launcher does. These pin the
    // rules GameLauncher had before the play flow moved behind IGameLauncher.
    public sealed class GameLauncherTests : IDisposable
    {
        private readonly TempGame _game = new();

        public GameLauncherTests() => GoProfile.Reset();

        private static GoProfile Profile => GoProfile.Current;

        private void Program(string exe) => _game.Write(@"game\" + exe, "");

        [Fact]
        public void Live_starts_the_live_client_and_Test_the_test_client_only_when_it_is_there()
        {
            Assert.Equal(Profile.LiveExe, GameLauncher.ExecutableFor(Channel.Live));
            Assert.Equal(Profile.LiveExe, GameLauncher.ExecutableFor(Channel.Test));
            Assert.False(GameLauncher.HasTestClient);

            Program(Profile.TestExe);
            Assert.True(GameLauncher.HasTestClient);
            Assert.Equal(Profile.TestExe, GameLauncher.ExecutableFor(Channel.Test));
        }

        [Fact]
        public void With_Easy_Anti_Cheat_chosen_the_channels_the_profile_names_go_through_its_launcher()
        {
            Program(Profile.TestExe);
            _game.Write(@"user\GeneralsOnlineData\settings.json", "{\"plugins\":{\"anticheat\":\"" + Profile.EacPlugin + "\"}}");

            Assert.Equal(Profile.TestThroughEac ? Profile.EacLauncherExe : Profile.TestExe, GameLauncher.ExecutableFor(Channel.Test));
            Assert.Equal(Profile.LiveThroughEac ? Profile.EacLauncherExe : Profile.LiveExe, GameLauncher.ExecutableFor(Channel.Live));
        }

        [Fact]
        public void Play_can_start_a_channel_only_when_its_program_is_in_the_game_folder()
        {
            Assert.False(GameLauncher.CanStart(Channel.Live));
            Assert.False(GameLauncher.HasOfficialLauncher);
            Program(Profile.LiveExe);
            Program(Profile.OfficialLauncherExe);
            Assert.True(GameLauncher.CanStart(Channel.Live));
            Assert.True(GameLauncher.HasOfficialLauncher);
        }

        [Fact]
        public void Windowed_mode_from_launcher_json_adds_the_window_size()
        {
            Assert.Empty(GameLauncher.Arguments());
            _game.Write(@"user\GeneralsOnlineData\launcher.json", """{"windowed":true,"windowed_width":1600,"windowed_height":900}""");
            Assert.Equal(["-win", "-xres", "1600", "-yres", "900"], GameLauncher.Arguments());
        }

        public void Dispose()
        {
            _game.Dispose();
            GoProfile.Reset();
        }
    }
}
