namespace CommandCenter.Tests
{
    // The match guard (seam 4, P5-D3): after an online match without a mismatch it keeps the game's archives and INI
    // files by name, size and date, and says what changed since then. Games and replays are built in the test.
    public sealed class MatchGuardTests : IDisposable
    {
        private readonly TempGame _game = new();

        private const string TwoPeople = "M=00maps/tournament desert;S=HAlice,0,8088,TT,1,2,-1,0,1:HBob,0,8088,TT,2,3,-1,1,1:X:X:X:X:X:X;";
        private const string AgainstTheComputer = "M=00maps/alpine assault;S=HAlice,0,8088,TT,1,2,-1,0,1:CH,3,4,-1,1:X:X:X:X:X:X;";

        public MatchGuardTests()
        {
            _game.Write(@"game\Data\INI\GameData.ini", "GameData\nEnd\n");
            _game.Write(@"game\ZH_Generals\INI.big", "BIGF");
        }

        // The game's last replay, as it writes one after every match
        private void Played(string options, uint start, bool mismatch = false) =>
            _game.Write(@"user\Replays\00000000.rep", ReplayHeaderTests.Replay(options, start, start + 600, mismatch));

        [Fact]
        public void A_good_online_match_records_the_files_and_nothing_has_changed_since()
        {
            Played(TwoPeople, 1_760_000_000);
            var changes = MatchGuard.Update();
            Assert.NotNull(changes);
            Assert.False(changes!.Any);
            Assert.True(File.Exists(Path.Combine(GamePaths.AppData, "match-guard.json")));
        }

        [Fact]
        public void Files_added_removed_and_changed_since_the_last_good_match_are_listed()
        {
            Played(TwoPeople, 1_760_000_000);
            MatchGuard.Update();

            _game.Write(@"game\Data\INI\Extra.ini", "Weapon\nEnd\n");
            File.Delete(Path.Combine(_game.Game, @"Data\INI\GameData.ini"));
            _game.Write(@"game\ZH_Generals\INI.big", "BIGF changed");

            var changes = MatchGuard.Update()!;
            Assert.Equal([@"Data\INI\Extra.ini"], changes.Added);
            Assert.Equal([@"Data\INI\GameData.ini"], changes.Removed);
            Assert.Equal([@"ZH_Generals\INI.big"], changes.Changed);
            Assert.False(changes.AfterMismatch);
        }

        [Fact]
        public void Generals_Online_own_files_are_left_out()
        {
            string patch = GoProfile.Current.CommunityPatchFile;
            _game.Write(@"game\" + patch, "BIGF");
            Played(TwoPeople, 1_760_000_000);
            MatchGuard.Update();

            _game.Write(@"game\" + patch, "BIGF newer patch");
            Assert.False(MatchGuard.Update()!.Any);
        }

        [Fact]
        public void A_match_against_the_computer_or_one_that_ended_in_a_mismatch_records_nothing()
        {
            Played(AgainstTheComputer, 1_760_000_000);
            Assert.Null(MatchGuard.Update());

            Played(TwoPeople, 1_760_001_000, mismatch: true);
            Assert.Null(MatchGuard.Update());
        }

        [Fact]
        public void After_a_match_that_ended_in_a_mismatch_the_changes_since_the_last_good_one_are_its_reason()
        {
            Played(TwoPeople, 1_760_000_000);
            MatchGuard.Update();
            _game.Write(@"game\ZH_Generals\INI.big", "BIGF changed");

            Played(TwoPeople, 1_760_001_000, mismatch: true);
            var changes = MatchGuard.Update()!;
            Assert.True(changes.AfterMismatch);
            Assert.Equal([@"ZH_Generals\INI.big"], changes.Changed);
        }

        [Fact]
        public void An_older_match_does_not_replace_the_one_kept()
        {
            Played(TwoPeople, 1_760_001_000);
            MatchGuard.Update();
            _game.Write(@"game\Data\INI\Extra.ini", "Weapon\nEnd\n");

            Played(TwoPeople, 1_760_000_000);
            Assert.Equal([@"Data\INI\Extra.ini"], MatchGuard.Update()!.Added);
        }

        [Fact]
        public void A_damaged_record_counts_as_none()
        {
            _game.Write(@"user\CommandCenter\match-guard.json", "{ not json");
            Assert.Null(MatchGuard.Update());
        }

        public void Dispose() => _game.Dispose();
    }
}
