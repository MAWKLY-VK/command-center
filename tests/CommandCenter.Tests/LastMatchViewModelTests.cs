using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Home's Last match panel (ticket 13): the newest replay with its map, when, length and mode, the recorder's result
    // once it is read, and Watch, which waits until the game is closed.
    public sealed class LastMatchViewModelTests
    {
        private readonly FakeShell _shell = new();
        private readonly List<ReplayEntry> _replays = [];
        private readonly List<ReplayEntry> _watched = [];
        private ReplayResults? _results;
        private Exception? _watchError;
        private bool _running;

        private static ReplayEntry Replay(string file, DateTime start, string map = "Tournament Desert") => new(
            file, "Title", map, @"maps\" + map,
            [new ReplayPlayer("Me", true, "", Faction.Usa, "", 0, 0), new ReplayPlayer("Them", true, "", Faction.China, "", 1, 1)],
            start, new TimeSpan(0, 12, 34), "1.04", 10000, false, false, false, 1000);

        private static ReplayResults Results(MatchResult? recorder, int index = 0) =>
            new([new ReplayPlayerResult("Me", null, null, recorder), new ReplayPlayerResult("Them", null, null, null)], new TimeSpan(0, 12, 34), index, true, true);

        private LastMatchViewModel Model() => new(() => _replays, _ => _results, r => r.Map, _ => null, r =>
        {
            if (_watchError != null)
                throw _watchError;
            _watched.Add(r);
        }, () => _running, _shell);

        [Fact]
        public async Task Without_replays_there_is_no_last_match()
        {
            var model = Model();
            await model.ShowAsync();
            Assert.False(model.HasMatch);
        }

        [Fact]
        public async Task The_newest_replay_shows_with_its_map_length_and_mode()
        {
            _replays.Add(Replay("old.rep", new DateTime(2026, 11, 1, 20, 0, 0), "Old Map"));
            _replays.Add(Replay("new.rep", new DateTime(2026, 11, 5, 21, 0, 0)));
            var model = Model();
            await model.ShowAsync();

            Assert.True(model.HasMatch);
            Assert.Equal("Tournament Desert", model.Map);
            Assert.EndsWith(", 12:34, 1v1", model.Info);

            _replays.Add(Replay("unknown.rep", new DateTime(2026, 11, 6, 9, 0, 0), ""));
            await model.ShowAsync();
            Assert.Equal("Unknown map", model.Map);
        }

        [Fact]
        public async Task The_recorders_result_shows_once_it_is_known()
        {
            _replays.Add(Replay("new.rep", new DateTime(2026, 11, 5, 21, 0, 0)));
            _results = Results(MatchResult.Won);
            var model = Model();
            await model.ShowAsync();
            Assert.Equal((true, "Won"), (model.HasResult, model.Result));

            _results = Results(MatchResult.Lost);
            await model.ShowAsync();
            Assert.Equal("Lost", model.Result);

            _results = Results(null);
            await model.ShowAsync();
            Assert.False(model.HasResult);

            _results = Results(MatchResult.Won, index: 5);
            await model.ShowAsync();
            Assert.False(model.HasResult);
        }

        [Fact]
        public async Task Watch_starts_the_replay_but_not_while_the_game_runs()
        {
            _replays.Add(Replay("new.rep", new DateTime(2026, 11, 5, 21, 0, 0)));
            var model = Model();
            await model.ShowAsync();

            _running = true;
            model.WatchCommand.Execute(null);
            Assert.Empty(_watched);
            Assert.Equal("Close the game first; a replay plays in the game itself.", _shell.Toasts[^1].Message);

            _running = false;
            model.WatchCommand.Execute(null);
            Assert.Single(_watched);

            _watchError = new IOException("The replay could not be copied");
            model.WatchCommand.Execute(null);
            Assert.Equal(("The replay could not be copied", true), (_shell.Toasts[^1].Message, _shell.Toasts[^1].IsError));
        }
    }
}
