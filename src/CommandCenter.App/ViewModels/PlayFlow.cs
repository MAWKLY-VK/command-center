namespace CommandCenter.ViewModels
{
    // Where Play stands: nothing running, the launch under way, or the game running (whoever started it)
    public enum PlayPhase { Idle, Starting, Running }

    // What PLAY does (ticket 13, P5-D2). PlayDecision says what the button is: Locate game opens the folder choice,
    // Update starts the official launcher, Fix applies the problem's fix with Undo (or opens its guide), and Play starts
    // the game. Play checks the player's files once more just before the launch, steps aside while the game runs and
    // comes back when it closes, and offers the official launcher whenever Command Center cannot start the game.
    // Windows is reached only through IGameLauncher and IShell.
    public sealed class PlayFlow
    {
        private readonly IGameLauncher _launcher;
        private readonly IGameFolder _game;
        private readonly IHealthChecks _checks;
        private readonly IServerStatus _server;
        private readonly FixRunner _fixes;
        private readonly IShell _shell;
        private readonly Func<Task<IReadOnlyList<HealthResult>>> _quickChecks;
        private readonly Action<string> _open;
        private readonly Action _afterGame;
        private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
        private bool _launching;

        // quickChecks: the fast checks of the player's own files (no network). afterGame: what follows a game that
        // closed (the replays and the match guard are read again).
        public PlayFlow(IGameLauncher launcher, IGameFolder game, IHealthChecks checks, IServerStatus server, FixRunner fixes, IShell shell,
            Func<Task<IReadOnlyList<HealthResult>>> quickChecks, Action<string> open, Action afterGame)
        {
            _launcher = launcher;
            _game = game;
            _checks = checks;
            _server = server;
            _fixes = fixes;
            _shell = shell;
            _quickChecks = quickChecks;
            _open = open;
            _afterGame = afterGame;
        }

        public PlayPhase Phase { get; private set; }

        // The phase changed
        public event Action? Changed;

        // What the button says and does now
        public PlayState Decide() => PlayDecision.Decide(new PlayInputs(_game.ZeroHourFound, _checks.Results, _server.Stats, _server.Asked));

        // PLAY pressed: what the button says
        public Task PressAsync(Channel channel)
        {
            var state = Decide();
            switch (state.Action)
            {
                case PlayAction.LocateGame:
                    Locate();
                    return Task.CompletedTask;
                case PlayAction.Update:
                    Update();
                    return Task.CompletedTask;
                case PlayAction.Fix:
                    Fix(state.Problem!);
                    return Task.CompletedTask;
                default:
                    return PlayAsync(channel, anyway: false);
            }
        }

        // The small link under Fix: the check never locks the player out
        public Task PlayAnywayAsync(Channel channel) => PlayAsync(channel, anyway: true);

        // What the watcher saw: a game started or closed outside Play
        public void SeeGame(bool running)
        {
            if (_launching)
                return;
            var now = running ? PlayPhase.Running : PlayPhase.Idle;
            if (now == Phase)
                return;
            bool closed = Phase == PlayPhase.Running;
            SetPhase(now);
            if (closed)
                _afterGame();
        }

        private void Locate()
        {
            if (_shell.ChooseFolder("Choose the Zero Hour folder", null) is not { } folder)
                return;
            if (!_game.Choose(folder))
            {
                _shell.Warn("That folder does not hold Zero Hour. Choose the folder with WindowZH.big and INIZH.big in it.");
                return;
            }
            _shell.Toast("Game folder chosen. The checks run again.");
        }

        // ADR 0001: the official launcher installs Generals Online's updates
        private void Update()
        {
            if (!_launcher.HasOfficialLauncher)
            {
                if (_shell.Confirm("The official Generals Online launcher installs the new version, but it is not in the game folder.\n\nOpen the Generals Online download page?"))
                    _open(HealthService.DownloadPage);
                return;
            }
            try
            {
                _launcher.StartOfficialLauncher();
            }
            catch (Exception ex)
            {
                _shell.Warn(string.Format("Failed to start the official launcher: {0}", ex.Message));
            }
        }

        // The problem's own fix with Undo; without one, its guide; else Health shows it
        private void Fix(HealthResult problem)
        {
            if (problem.Fix != null && problem.FixLabel != null)
                FixActions.ApplyOne(_fixes, _shell, problem, Recheck);
            else if (problem.GuideTarget == HealthService.QuestionsTarget)
                _shell.ShowHelp();
            else if (problem.GuideTarget is { } target)
                _open(target);
            else
                _shell.ShowTools("health");
        }

        private async Task PlayAsync(Channel channel, bool anyway)
        {
            if (_launching || Phase != PlayPhase.Idle)
                return;
            _launching = true;
            bool started = true, launched = false, stepped = false;
            try
            {
                if (!_game.GeneralsOnlineFound)
                {
                    if (_shell.Confirm(string.Format("Generals Online is not installed in the game folder:\n{0}\n\nInstall it, then press Play again. Open the Generals Online download page?", _game.Path)))
                        _open(HealthService.DownloadPage);
                    return;
                }
                if (_launcher.IsRunning())
                {
                    SetPhase(PlayPhase.Running);
                    _shell.Toast("The game is already running.");
                    return;
                }
                // The player's files once more, just before the launch: a problem that came since the last check stops it
                if (!anyway && PlayDecision.Blocking(await _quickChecks()) is [var problem, ..])
                {
                    _shell.Toast(string.Format("A problem was found just now: {0}. Fix it first, or choose Play anyway.", problem.Title));
                    _ = Recheck();
                    return;
                }
                if (!_launcher.CanStart(channel))
                {
                    OfferOfficialLauncher(string.Format("{0} is not in the game folder, so Command Center cannot start this version of Generals Online.",
                        _launcher.ExecutableFor(channel)));
                    return;
                }

                launched = true;
                SetPhase(PlayPhase.Starting);
                _launcher.Start(channel);
                // Stays open under the game while it starts (it hands the focus back to the game if Windows gives it here),
                // then steps aside without taking the focus; back when the game closes
                started = await _launcher.WaitAsync(_shell.WindowHandle, () => OnUi(() =>
                {
                    stepped = true;
                    SetPhase(PlayPhase.Running);
                    _shell.StepAside();
                }));
                if (stepped)
                    _shell.ComeBack();
                _afterGame();
            }
            catch (Exception ex)
            {
                if (stepped)
                    _shell.ComeBack();
                _shell.Warn(string.Format("Failed to launch the game: {0}", ex.Message));
            }
            finally
            {
                _launching = false;
                if (launched)
                    SetPhase(PlayPhase.Idle);
            }
            if (!started)
                OfferOfficialLauncher("The game did not start. What happened is written in play.log in Command Center's folder.");
        }

        // When Play cannot start the game, the official launcher, which knows its own programs, is offered instead
        private void OfferOfficialLauncher(string reason)
        {
            if (!_launcher.HasOfficialLauncher)
            {
                if (_shell.Confirm(reason + "\n\nOpen the Generals Online download page?"))
                    _open(HealthService.DownloadPage);
                return;
            }
            if (!_shell.Confirm(reason + "\n\nStart the official Generals Online launcher instead?"))
                return;
            try
            {
                _launcher.StartOfficialLauncher();
            }
            catch (Exception ex)
            {
                _shell.Warn(string.Format("Failed to launch the game: {0}", ex.Message));
            }
        }

        private async Task Recheck()
        {
            try
            {
                await _checks.CheckAsync();
            }
            catch (Exception ex)
            {
                _shell.Toast(string.Format("Something went wrong: {0}", ex.Message), isError: true);
            }
        }

        private void SetPhase(PlayPhase phase)
        {
            if (Phase == phase)
                return;
            Phase = phase;
            Changed?.Invoke();
        }

        // The game's wait can call back from elsewhere; the flow changes on the thread that made it
        private void OnUi(Action action)
        {
            if (_ui == null || SynchronizationContext.Current == _ui)
                action();
            else
                _ui.Post(_ => action(), null);
        }
    }
}
