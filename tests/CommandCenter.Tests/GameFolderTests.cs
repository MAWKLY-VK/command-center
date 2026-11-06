namespace CommandCenter.Tests
{
    // The game folder module (ticket 08): where the game is, where that came from, and the player's choice of another
    // folder, which everything that depends on the game hears about.
    public sealed class GameFolderTests : IDisposable
    {
        private readonly TempGame _game = new();
        private readonly GameFolder _folder = new();

        // Another copy of Zero Hour next to the test's own
        private string OtherCopy(string name)
        {
            string folder = Path.Combine(_game.Root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "WindowZH.big"), []);
            File.WriteAllBytes(Path.Combine(folder, "INIZH.big"), []);
            return folder;
        }

        [Fact]
        public void A_Zero_Hour_folder_the_player_chooses_is_used_remembered_and_announced()
        {
            string other = OtherCopy("other");
            int changes = 0;
            _folder.Changed += () => changes++;

            Assert.True(_folder.Choose(other));
            Assert.Equal(other, _folder.Path);
            Assert.Equal(GameFolderSource.ChosenByPlayer, _folder.Source);
            Assert.Equal(other, AppSettings.Current.GameFolder);
            Assert.Equal(1, changes);
        }

        [Fact]
        public void A_folder_without_Zero_Hour_is_refused_and_nothing_changes()
        {
            string before = _folder.Path;
            int changes = 0;
            _folder.Changed += () => changes++;

            Assert.False(_folder.Choose(Path.Combine(_game.Root, "empty")));
            Assert.Equal(before, _folder.Path);
            Assert.Equal(0, changes);
        }

        [Fact]
        public void The_source_says_where_the_folder_came_from()
        {
            Assert.Equal(GameFolderSource.Other, _folder.Source);
            Assert.False(_folder.GeneralsOnlineFound);

            GamePaths.Game = OtherCopy(@"steamapps\common\Command & Conquer Generals - Zero Hour");
            Assert.Equal(GameFolderSource.Steam, _folder.Source);

            GamePaths.Game = "";
            Assert.Equal((GameFolderSource.NotFound, ""), (_folder.Source, _folder.Path));
        }

        [Fact]
        public void The_match_guard_keeps_a_record_per_game_folder()
        {
            _game.Write(@"user\Replays\00000000.rep", ReplayHeaderTests.Replay(
                "M=00maps/tournament desert;S=HAlice,0,8088,TT,1,2,-1,0,1:HBob,0,8088,TT,2,3,-1,1,1:X:X:X:X:X:X;"));
            Assert.NotNull(MatchGuard.Update());

            // The same replay says nothing about another copy's files
            GamePaths.Game = OtherCopy("other");
            Assert.Null(MatchGuard.Update());
        }

        public void Dispose()
        {
            AppSettings.Current.GameFolder = null;
            _game.Dispose();
        }
    }
}
