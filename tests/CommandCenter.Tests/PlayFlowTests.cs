using System.Text;
using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Home (ticket 13): what PLAY does in each state (P5-D2), and the steps of Play that the Home page held before the
    // move: the checks just before the launch, stepping aside while the game runs, and the official launcher offered
    // when Command Center cannot start the game.
    public sealed class PlayFlowTests : IDisposable
    {
        private readonly TempGame _game = new();
        private readonly FakeShell _shell = new();
        private readonly FakeGameFolder _folder = new();
        private readonly FakeLauncher _launcher = new();
        private readonly List<string> _opened = [];
        private IReadOnlyList<HealthResult> _health = [];
        private IReadOnlyList<HealthResult> _quick = [];
        private int _checks;
        private int _afterGame;

        private PlayFlow Flow(out HealthChecks checks)
        {
            checks = new HealthChecks(() =>
            {
                _checks++;
                return Task.FromResult(_health);
            }, () => DateTime.Now);
            var server = new ServerStatus(() => Task.FromResult<ServerStats?>(new ServerStats(335, 88, 70)));
            // The servers have answered once, as at start
            server.RefreshAsync().GetAwaiter().GetResult();
            return new PlayFlow(_launcher, _folder, checks, server, new FixRunner(() => { }), _shell, () => Task.FromResult(_quick),
                _opened.Add, () => _afterGame++);
        }

        private static HealthResult Blocking(string id, HealthImpact impact = HealthImpact.StopsGame, Action? fix = null) => new()
        {
            Id = id, Group = HealthService.GroupFiles, Title = "Title " + id, Detail = "", Status = HealthStatus.Problem, Impact = impact,
            Fix = fix, FixLabel = fix != null ? "Quarantine" : null,
        };

        [Fact]
        public async Task Locate_game_opens_the_folder_choice_and_a_wrong_folder_is_refused()
        {
            _folder.ZeroHourFound = false;
            _folder.Accept = false;
            _shell.FolderToPick = @"C:\Wrong";
            var flow = Flow(out _);
            Assert.Equal(PlayAction.LocateGame, flow.Decide().Action);

            await flow.PressAsync(Channel.Live);
            Assert.Equal(["That folder does not hold Zero Hour. Choose the folder with WindowZH.big and INIZH.big in it."], _shell.Warnings);

            _folder.Accept = true;
            _shell.FolderToPick = @"C:\Games\Zero Hour";
            await flow.PressAsync(Channel.Live);
            Assert.Equal([@"C:\Games\Zero Hour"], _folder.Chosen);
            Assert.Empty(_launcher.Started);
        }

        [Fact]
        public async Task Update_starts_the_official_launcher_or_offers_the_download_page()
        {
            _health = [new HealthResult { Id = HealthService.GoVersionId, Group = HealthService.GroupNetwork, Title = "New version", Detail = "", Status = HealthStatus.Warning }];
            var flow = Flow(out var checks);
            await checks.CheckAsync();
            Assert.Equal(PlayAction.Update, flow.Decide().Action);

            await flow.PressAsync(Channel.Live);
            Assert.Equal(1, _launcher.OfficialStarted);

            _launcher.HasOfficialLauncher = false;
            await flow.PressAsync(Channel.Live);
            Assert.Contains("Open the Generals Online download page?", _shell.Questions[^1]);
            Assert.Equal([HealthService.DownloadPage], _opened);
        }

        [Fact]
        public async Task Fix_applies_the_problems_fix_with_undo_and_checks_again()
        {
            string file = _game.Write(@"game\Options.ini", "before");
            _health = [Blocking("ini", fix: () => BackupService.WriteFile("Fixed Options.ini", file, Encoding.UTF8.GetBytes("after")))];
            var flow = Flow(out var checks);
            await checks.CheckAsync();
            var state = flow.Decide();
            Assert.Equal((PlayAction.Fix, true), (state.Action, state.PlayAnyway));

            await flow.PressAsync(Channel.Live);
            Assert.Equal("after", File.ReadAllText(file));
            Assert.Equal("Fixed: Title ini", _shell.Toasts[^1].Message);
            Assert.Equal(2, _checks);
            _shell.Toasts[^1].Undo!();
            Assert.Equal("before", File.ReadAllText(file));
            Assert.Empty(_launcher.Started);
        }

        [Fact]
        public async Task Play_starts_the_chosen_channel_steps_aside_while_the_game_runs_and_comes_back()
        {
            var flow = Flow(out var checks);
            await checks.CheckAsync();
            var phases = new List<PlayPhase>();
            flow.Changed += () => phases.Add(flow.Phase);
            Assert.Equal(("Play", "Ready. 335 players online."), (flow.Decide().Label, flow.Decide().Status));

            await flow.PressAsync(Channel.Test);
            Assert.Equal([Channel.Test], _launcher.Started);
            Assert.Equal((1, 1, 1), (_shell.SteppedAside, _shell.CameBack, _afterGame));
            Assert.Equal([PlayPhase.Starting, PlayPhase.Running, PlayPhase.Idle], phases);
        }

        [Fact]
        public async Task A_problem_found_just_before_the_launch_stops_it_and_Play_anyway_goes_ahead()
        {
            var flow = Flow(out var checks);
            await checks.CheckAsync();
            _quick = [Blocking("new", HealthImpact.Mismatch)];

            await flow.PressAsync(Channel.Live);
            Assert.Empty(_launcher.Started);
            Assert.Equal("A problem was found just now: Title new. Fix it first, or choose Play anyway.", _shell.Toasts[^1].Message);
            Assert.Equal(2, _checks);

            await flow.PlayAnywayAsync(Channel.Live);
            Assert.Equal([Channel.Live], _launcher.Started);
        }

        [Fact]
        public async Task A_missing_program_or_a_game_that_does_not_start_offers_the_official_launcher()
        {
            var flow = Flow(out var checks);
            await checks.CheckAsync();
            _launcher.Startable = false;
            await flow.PressAsync(Channel.Live);
            Assert.Equal("GeneralsOnlineZH_60.exe is not in the game folder, so Command Center cannot start this version of Generals Online.\n\nStart the official Generals Online launcher instead?",
                _shell.Questions[^1]);
            Assert.Equal(1, _launcher.OfficialStarted);

            _launcher.Startable = true;
            _launcher.Wait = _ => Task.FromResult(false);
            _shell.Answer = false;
            await flow.PressAsync(Channel.Live);
            Assert.StartsWith("The game did not start. What happened is written in play.log in Command Center's folder.", _shell.Questions[^1]);
            Assert.Equal(1, _launcher.OfficialStarted);
            Assert.Equal(0, _shell.SteppedAside);
        }

        [Fact]
        public async Task A_game_already_running_or_not_installed_is_not_started()
        {
            var flow = Flow(out var checks);
            await checks.CheckAsync();
            _launcher.Running = true;
            await flow.PressAsync(Channel.Live);
            Assert.Equal(("The game is already running.", PlayPhase.Running), (_shell.Toasts[^1].Message, flow.Phase));

            _launcher.Running = false;
            flow.SeeGame(false);
            _folder.GeneralsOnlineFound = false;
            await flow.PressAsync(Channel.Live);
            Assert.Contains("Generals Online is not installed in the game folder", _shell.Questions[^1]);
            Assert.Equal([HealthService.DownloadPage], _opened);
            Assert.Empty(_launcher.Started);
        }

        [Fact]
        public async Task A_launch_that_fails_says_why_and_a_game_closed_elsewhere_refreshes_the_last_match()
        {
            var flow = Flow(out var checks);
            await checks.CheckAsync();
            _launcher.StartError = new InvalidOperationException("Access denied");
            await flow.PressAsync(Channel.Live);
            Assert.Equal(["Failed to launch the game: Access denied"], _shell.Warnings);
            Assert.Equal(PlayPhase.Idle, flow.Phase);

            flow.SeeGame(true);
            Assert.Equal(PlayPhase.Running, flow.Phase);
            flow.SeeGame(false);
            Assert.Equal((PlayPhase.Idle, 1), (flow.Phase, _afterGame));
        }

        public void Dispose() => _game.Dispose();
    }
}
