namespace CommandCenter.Tests
{
    // Interface sounds (ticket 14): nothing is read or played while the setting is off; when it is on, the click and the
    // launch play the game's sounds, read once, and read again after the game folder changes.
    public sealed class InterfaceSoundPlayerTests
    {
        private bool _enabled;
        private int _reads;
        private GameSounds.Set _sounds = new([1, 2, 3], [4, 5, 6]);
        private readonly List<byte[]> _played = [];

        private InterfaceSoundPlayer Player() => new(() => _enabled, () =>
        {
            _reads++;
            return _sounds;
        }, _played.Add);

        [Fact]
        public async Task Nothing_is_read_or_played_while_the_setting_is_off()
        {
            var player = Player();
            await player.PlayClickAsync();
            await player.PlayLaunchAsync();
            Assert.Equal(0, _reads);
            Assert.Empty(_played);
        }

        [Fact]
        public async Task When_on_the_click_and_the_launch_play_the_games_sounds_read_once()
        {
            _enabled = true;
            var player = Player();
            await player.PlayClickAsync();
            await player.PlayLaunchAsync();
            await player.PlayClickAsync();
            Assert.Equal([_sounds.Click!, _sounds.Launch!, _sounds.Click!], _played);
            Assert.Equal(1, _reads);
        }

        [Fact]
        public async Task A_game_without_a_sound_plays_nothing_and_another_folder_is_read_again()
        {
            _enabled = true;
            _sounds = new(null, null);
            var player = Player();
            await player.PlayClickAsync();
            Assert.Empty(_played);

            _sounds = new([7], null);
            player.Forget();
            await player.PlayClickAsync();
            Assert.Equal((2, 1), (_reads, _played.Count));
        }

        [Fact]
        public async Task A_read_that_fails_is_logged_once_not_on_every_click()
        {
            _enabled = true;
            string marker = "The sounds could not be read " + Guid.NewGuid().ToString("N");
            var player = new InterfaceSoundPlayer(() => _enabled, () =>
            {
                _reads++;
                throw new IOException(marker);
            }, _played.Add);

            await player.PlayClickAsync();
            await player.PlayClickAsync();
            Assert.Empty(_played);
            Assert.Equal(1, _reads);
            string log = File.Exists(ErrorLog.FilePath) ? File.ReadAllText(ErrorLog.FilePath) : "";
            Assert.Equal(1, log.Split(marker).Length - 1);
        }

        [Fact]
        public async Task Prepare_reads_the_sounds_ahead_only_while_on()
        {
            var player = Player();
            await player.PrepareAsync();
            Assert.Equal(0, _reads);

            _enabled = true;
            await player.PrepareAsync();
            Assert.Equal(1, _reads);
            await player.PlayClickAsync();
            Assert.Equal((1, 1), (_reads, _played.Count));
        }
    }
}
