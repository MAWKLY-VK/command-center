namespace CommandCenter.Tests
{
    // PLAY's state (seam 3, P5-D2): Locate game, Update, Fix or Play in that priority, the line under the button, Play
    // anyway, and which Health results the quick check acts on.
    public sealed class PlayStateTests
    {
        private static HealthResult Result(string id, HealthStatus status, HealthImpact impact, string title) =>
            new() { Id = id, Group = HealthService.GroupFiles, Title = title, Detail = "", Status = status, Impact = impact };

        private static readonly HealthResult UpdateWaiting =
            Result(HealthService.GoVersionId, HealthStatus.Warning, HealthImpact.None, "Generals Online is not the current version");
        private static readonly HealthResult UpToDate =
            Result(HealthService.GoVersionId, HealthStatus.Passed, HealthImpact.None, "Generals Online is up to date");
        private static readonly HealthResult PluginMissing =
            Result("acplugin", HealthStatus.Problem, HealthImpact.StopsGame, "Anti-cheat plugin is missing");
        private static readonly HealthResult AdminFlag =
            Result("admin", HealthStatus.Warning, HealthImpact.StopsGame, "\"Run as administrator\" is set on the game");
        private static readonly HealthResult PatchMissing =
            Result("datapatch", HealthStatus.Problem, HealthImpact.Mismatch, "Community data patch file is missing");
        private static readonly HealthResult OneDrive =
            Result("onedrive", HealthStatus.Warning, HealthImpact.None, "Documents folder is in OneDrive");

        private static PlayState Decide(bool gameFound, HealthResult[] health, ServerStats? server = null, bool serverAsked = false) =>
            PlayDecision.Decide(new PlayInputs(gameFound, health, server, serverAsked));

        // ── The state and its priority ──

        [Fact]
        public void Without_the_game_PLAY_locates_it_whatever_else_applies()
        {
            var state = Decide(gameFound: false, [UpdateWaiting, PluginMissing]);
            Assert.Equal((PlayAction.LocateGame, "Locate game"), (state.Action, state.Label));
            Assert.Equal("Zero Hour was not found. Choose its folder.", state.Status);
        }

        [Fact]
        public void A_new_Generals_Online_version_comes_before_a_fix()
        {
            var state = Decide(gameFound: true, [PluginMissing, UpdateWaiting]);
            Assert.Equal((PlayAction.Update, "Update"), (state.Action, state.Label));
            Assert.Equal("Generals Online has a new version. The official launcher installs it.", state.Status);
        }

        [Fact]
        public void A_problem_that_stops_the_game_turns_PLAY_into_Fix_with_Play_anyway()
        {
            var state = Decide(gameFound: true, [UpToDate, PluginMissing]);
            Assert.Equal((PlayAction.Fix, "Fix"), (state.Action, state.Label));
            Assert.Equal("Anti-cheat plugin is missing.", state.Status);
            Assert.Same(PluginMissing, state.Problem);
            Assert.True(state.PlayAnyway);
        }

        [Fact]
        public void A_mismatch_turns_PLAY_into_Fix_as_well()
        {
            var state = Decide(gameFound: true, [PatchMissing]);
            Assert.Equal(PlayAction.Fix, state.Action);
            Assert.Same(PatchMissing, state.Problem);
        }

        [Fact]
        public void What_stops_the_game_is_fixed_before_a_mismatch_and_the_line_counts_the_rest()
        {
            Assert.Equal("Anti-cheat plugin is missing, and 1 more problem.",
                Decide(gameFound: true, [PatchMissing, OneDrive, PluginMissing]).Status);
            Assert.Equal("Anti-cheat plugin is missing, and 2 more problems.",
                Decide(gameFound: true, [PatchMissing, AdminFlag, PluginMissing]).Status);
        }

        [Fact]
        public void Warnings_that_neither_stop_the_game_nor_cause_a_mismatch_leave_PLAY_as_Play()
        {
            var state = Decide(gameFound: true, [OneDrive, UpToDate]);
            Assert.Equal((PlayAction.Play, "Play"), (state.Action, state.Label));
            Assert.Null(state.Problem);
        }

        [Fact]
        public void Only_Fix_offers_Play_anyway()
        {
            Assert.False(Decide(gameFound: false, []).PlayAnyway);
            Assert.False(Decide(gameFound: true, [UpdateWaiting]).PlayAnyway);
            Assert.False(Decide(gameFound: true, []).PlayAnyway);
        }

        // ── The line under Play ──

        [Fact]
        public void Play_says_how_many_players_are_online()
        {
            Assert.Equal("Ready. 335 players online.", Decide(gameFound: true, [], new ServerStats(335, 88, 120), serverAsked: true).Status);
            Assert.Equal("Ready. 1 player online.", Decide(gameFound: true, [], new ServerStats(1, 0, 40), serverAsked: true).Status);
        }

        [Fact]
        public void Play_says_when_the_servers_do_not_answer() =>
            Assert.Equal("Ready. The Generals Online servers do not answer right now.", Decide(gameFound: true, [], null, serverAsked: true).Status);

        [Fact]
        public void Before_the_servers_answer_Play_is_simply_ready() =>
            Assert.Equal("Ready.", Decide(gameFound: true, [], null, serverAsked: false).Status);

        // ── The quick check ──

        [Fact]
        public void The_quick_check_takes_the_failing_results_that_stop_the_game_or_cause_a_mismatch_the_worst_first()
        {
            var passedPlugin = Result("acplugin", HealthStatus.Passed, HealthImpact.StopsGame, "Anti-cheat plugin in place");
            Assert.Equal([PluginMissing, AdminFlag, PatchMissing],
                PlayDecision.Blocking([OneDrive, PatchMissing, passedPlugin, AdminFlag, UpdateWaiting, PluginMissing]));
        }

        [Fact]
        public void Health_says_where_a_result_is_made_whether_it_stops_the_game_or_causes_a_mismatch()
        {
            // A bare copy: Generals Online is not installed and Options.ini is not made yet
            using var game = new TempGame();
            var bare = HealthService.Run();
            Assert.Equal(HealthImpact.StopsGame, bare.Single(r => r.Id == "files").Impact);
            Assert.Equal(HealthImpact.None, bare.Single(r => r.Id == "options").Impact);

            // With the client in place the community patch is checked too, and it is missing
            File.WriteAllBytes(Path.Combine(game.Game, GamePaths.GameExe), []);
            Assert.Equal(HealthImpact.Mismatch, HealthService.Run().Single(r => r.Id == "datapatch").Impact);
        }
    }
}
