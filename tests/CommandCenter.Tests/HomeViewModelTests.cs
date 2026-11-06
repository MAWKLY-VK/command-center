using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Home on a view model (ticket 13): PLAY's words and the line under it in each state, the data line, the match
    // guard's news, the Live/Test choice (offered only with the test client, saved when chosen) and the Health panel.
    public sealed class HomeViewModelTests : IDisposable
    {
        private readonly TempGame _game = new();
        private readonly FakeShell _shell = new();
        private readonly FakeGameFolder _folder = new();
        private readonly FakeLauncher _launcher = new();
        private IReadOnlyList<HealthResult> _health = [];
        private MatchChanges? _changes;
        private bool _preferLive = true;
        private readonly List<bool> _saved = [];
        private readonly List<HomeViewModel> _models = [];

        private async Task<HomeViewModel> Model()
        {
            var checks = new HealthChecks(() => Task.FromResult(_health), () => DateTime.Now);
            var server = new ServerStatus(() => Task.FromResult<ServerStats?>(new ServerStats(335, 88, 70)));
            await server.RefreshAsync();
            await checks.CheckAsync();
            var play = new PlayFlow(_launcher, _folder, checks, server, new FixRunner(() => { }), _shell,
                () => Task.FromResult<IReadOnlyList<HealthResult>>([]), _ => { }, () => { });
            var lastMatch = new LastMatchViewModel(() => [], _ => null, r => r.Map, _ => null, _ => { }, () => false, _shell);
            var model = new HomeViewModel(play, checks, server, _launcher, lastMatch, _shell, () => "100326", () => _changes,
                () => _preferLive, live =>
                {
                    _saved.Add(live);
                    _preferLive = live;
                });
            _models.Add(model);
            return model;
        }

        [Fact]
        public async Task PLAY_follows_the_state_and_the_line_under_it_says_why()
        {
            _folder.ZeroHourFound = false;
            var model = await Model();
            Assert.Equal(("Locate game", "folder", "Zero Hour was not found. Choose its folder.", true),
                (model.PlayLabel, model.PlayIcon, model.Status, model.ShowGetGame));

            _folder.ZeroHourFound = true;
            model = await Model();
            Assert.Equal(("Play", "play", "Ready. 335 players online.", false, true),
                (model.PlayLabel, model.PlayIcon, model.Status, model.ShowGetGame, model.CanPlay));
            Assert.Equal(("Generals Online 100326", "335 online", "70 ms ping"), (model.VersionText, model.PlayersText, model.PingText));
        }

        [Fact]
        public async Task Fix_offers_Play_anyway_and_PLAY_waits_while_the_game_starts_and_runs()
        {
            _health = [new HealthResult { Id = "dll", Group = HealthService.GroupFiles, Title = "Old Visual C++ file", Detail = "", Status = HealthStatus.Problem, Impact = HealthImpact.StopsGame }];
            var model = await Model();
            Assert.Equal(("Fix", "wrench", "Old Visual C++ file.", true), (model.PlayLabel, model.PlayIcon, model.Status, model.ShowPlayAnyway));

            (string, string, bool)? whileRunning = null;
            _launcher.Wait = settled =>
            {
                settled();
                whileRunning = (model.PlayLabel, model.Status, model.CanPlay);
                return Task.FromResult(true);
            };
            await model.PlayAnywayCommand.ExecuteAsync(null);
            Assert.Equal(("Running", "The game is running.", false), whileRunning);
            Assert.Equal(("Fix", true), (model.PlayLabel, model.CanPlay));
        }

        [Fact]
        public async Task Files_changed_since_the_last_good_match_are_named_under_PLAY()
        {
            _changes = new MatchChanges([@"Data\INI\Weapon.ini"], [], ["INIZH.big", "WindowZH.big", "TexturesZH.big"], AfterMismatch: false);
            var model = await Model();
            Assert.Equal(("Play", true), (model.PlayLabel, model.ShowGuard));
            Assert.Equal(@"Changed since your last good match: INIZH.big, WindowZH.big, TexturesZH.big and 1 more.", model.GuardText);

            _changes = new MatchChanges([], [], ["INIZH.big"], AfterMismatch: true);
            model.RefreshGuard();
            Assert.Equal("Your last match ended in a mismatch. Changed since your last good match: INIZH.big.", model.GuardText);

            _changes = new MatchChanges([], [], [], AfterMismatch: true);
            model.RefreshGuard();
            Assert.Equal("Your last match ended in a mismatch. Your game's files did not change since your last good match.", model.GuardText);

            model.ReviewGuardCommand.Execute(null);
            Assert.Equal(["health"], _shell.ToolsShown);

            _changes = new MatchChanges([], [], [], AfterMismatch: false);
            model.RefreshGuard();
            Assert.False(model.ShowGuard);
        }

        [Fact]
        public async Task Live_or_Test_is_offered_only_with_the_test_client_and_saved_when_chosen()
        {
            var model = await Model();
            Assert.False(model.ShowChannels);

            _launcher.HasTestClient = true;
            _preferLive = false;
            model = await Model();
            Assert.Equal((true, false, true), (model.ShowChannels, model.IsLive, model.IsTest));
            await model.PlayCommand.ExecuteAsync(null);
            Assert.Equal([Channel.Test], _launcher.Started);

            model.IsLive = true;
            Assert.Equal([true], _saved);
            await model.PlayCommand.ExecuteAsync(null);
            Assert.Equal([Channel.Test, Channel.Live], _launcher.Started);
        }

        [Fact]
        public async Task The_Health_panel_reads_the_report_and_opens_Health()
        {
            _health = [new HealthResult { Id = "w", Group = HealthService.GroupFiles, Title = "Warning", Detail = "", Status = HealthStatus.Warning }];
            var model = await Model();
            Assert.Equal(("1 warning found", false, false), (model.HealthLine, model.HealthIsClear, model.HealthIsProblem));
            model.ReviewHealthCommand.Execute(null);
            Assert.Equal(["health"], _shell.ToolsShown);

            _health = [];
            model = await Model();
            Assert.Equal(("Your game passed every check", true), (model.HealthLine, model.HealthIsClear));
        }

        public void Dispose()
        {
            foreach (var model in _models)
                model.Dispose();
            _game.Dispose();
        }
    }
}
