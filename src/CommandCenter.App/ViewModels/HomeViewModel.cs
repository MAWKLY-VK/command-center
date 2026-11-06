using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommandCenter.ViewModels
{
    // Home, the Briefing stage, on a view model (ticket 13): PLAY with the words of its state and the line under it, the
    // match guard's news, the data line (version, players, ping), the Live/Test choice, and the Last match and Health
    // panels. PlayFlow does what PLAY says; the numbers come from IServerStatus and the results from IHealthChecks.
    public sealed partial class HomeViewModel : ObservableObject, IDisposable
    {
        private readonly PlayFlow _play;
        private readonly IHealthChecks _checks;
        private readonly IServerStatus _server;
        private readonly IGameLauncher _launcher;
        private readonly IShell _shell;
        private readonly Func<string> _clientVersion;
        private readonly Func<MatchChanges?> _matchChanges;
        private readonly Func<bool> _preferLive;
        private readonly Action<bool> _savePreferLive;
        private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
        private readonly Action _onChange;
        private PlayState _state;
        private bool _live;
        private bool _preview;

        public HomeViewModel(PlayFlow play, IHealthChecks checks, IServerStatus server, IGameLauncher launcher, LastMatchViewModel lastMatch,
            IShell shell, Func<string> clientVersion, Func<MatchChanges?> matchChanges, Func<bool> preferLive, Action<bool> savePreferLive)
        {
            _play = play;
            _checks = checks;
            _server = server;
            _launcher = launcher;
            LastMatch = lastMatch;
            _shell = shell;
            _clientVersion = clientVersion;
            _matchChanges = matchChanges;
            _preferLive = preferLive;
            _savePreferLive = savePreferLive;
            _live = preferLive();
            _state = play.Decide();
            _onChange = () => OnUi(Refresh);
            play.Changed += _onChange;
            checks.Changed += _onChange;
            server.Changed += _onChange;
            RefreshGuard();
        }

        public LastMatchViewModel LastMatch { get; }

        // ── PLAY ──

        public bool CanPlay => _play.Phase == PlayPhase.Idle;

        public string PlayLabel => _play.Phase switch
        {
            PlayPhase.Starting => "Starting",
            PlayPhase.Running => "Running",
            _ => _state.Label,
        };

        // The button's sign: what pressing it does
        public string PlayIcon => _play.Phase != PlayPhase.Idle ? "play" : _state.Action switch
        {
            PlayAction.LocateGame => "folder",
            PlayAction.Update => "download",
            PlayAction.Fix => "wrench",
            _ => "play",
        };

        public string Status => _play.Phase switch
        {
            PlayPhase.Starting => "Starting the game…",
            PlayPhase.Running => "The game is running.",
            _ => _state.Status,
        };

        public bool ShowPlayAnyway => CanPlay && _state.PlayAnyway;
        public bool ShowGetGame => CanPlay && _state.Action == PlayAction.LocateGame;

        // Live or Test: offered when the test client is in the game folder, and kept in launcher.json
        public bool ShowChannels => _launcher.HasTestClient;

        public bool IsLive
        {
            get => _live;
            set
            {
                if (value)
                    Choose(live: true);
            }
        }

        public bool IsTest
        {
            get => !_live;
            set
            {
                if (value)
                    Choose(live: false);
            }
        }

        private Channel Channel => ShowChannels && !_live ? Channel.Test : Channel.Live;

        // The channel PLAY starts, in the data line when there is a choice
        public string? ChannelText => ShowChannels ? (_live ? "Live" : "Test") : null;

        [RelayCommand]
        private Task PlayAsync() => _play.PressAsync(Channel);

        [RelayCommand]
        private Task PlayAnywayAsync() => _play.PlayAnywayAsync(Channel);

        // Where to get Zero Hour, under Locate game
        [RelayCommand]
        private void GetGame() => _shell.ShowHelp();

        private void Choose(bool live)
        {
            if (_live == live)
                return;
            _live = live;
            OnPropertyChanged(nameof(IsLive));
            OnPropertyChanged(nameof(IsTest));
            try
            {
                _savePreferLive(live);
            }
            catch (Exception ex)
            {
                _shell.Toast(string.Format("Could not save: {0}", ex.Message), isError: true);
            }
        }

        // ── The match guard (P5-D3) ──

        public bool ShowGuard { get; private set; }
        public string GuardText { get; private set; } = "";

        // The files that decide whether players match, changed since the last online match without a mismatch; after a
        // match that ended in one, said even when nothing changed
        public void RefreshGuard()
        {
            var changes = _matchChanges();
            ShowGuard = changes is { Any: true } or { AfterMismatch: true };
            if (changes == null || !ShowGuard)
                GuardText = "";
            else if (!changes.Any)
                GuardText = "Your last match ended in a mismatch. Your game's files did not change since your last good match.";
            else
            {
                string files = English.List(changes.Changed.Concat(changes.Added).Concat(changes.Removed), show: 3);
                GuardText = (changes.AfterMismatch ? "Your last match ended in a mismatch. " : "") + string.Format("Changed since your last good match: {0}.", files);
            }
            OnPropertyChanged(nameof(ShowGuard));
            OnPropertyChanged(nameof(GuardText));
        }

        [RelayCommand]
        private void ReviewGuard() => _shell.ShowTools("health");

        // ── The data line: real numbers only ──

        public string VersionText => "Generals Online " + _clientVersion();
        public string? PlayersText => _server.Stats is { } stats ? stats.Players.ToString("N0", CultureInfo.CurrentCulture) + " online" : null;
        public string? PingText => _server.Stats is { LatencyMs: > 0 } stats ? stats.LatencyMs.ToString(CultureInfo.CurrentCulture) + " ms ping" : null;
        public bool HasPlayers => PlayersText != null;
        public bool HasPing => PingText != null;

        // ── The Health panel ──

        public bool HealthReady => _checks.Report.IsReady;
        public string HealthLine => _checks.Report.HomeLine;
        public bool HealthIsClear => _checks.Report.Issues == 0;
        public bool HealthIsProblem => _checks.Report.Problems > 0;
        public bool HealthHasIssues => HealthReady && !HealthIsClear;
        public string HealthText => HealthReady ? HealthLine : "Checking your game…";

        // The panel's sign: checking, all clear, a warning, or a problem
        public string HealthSign => !HealthReady ? "clock" : HealthIsClear ? "check-circle" : HealthIsProblem ? "x-circle" : "alert";

        [RelayCommand]
        private void ReviewHealth() => _shell.ShowTools("health");

        // ── Updates ──

        // The game, the checks or the servers changed
        public void Refresh()
        {
            if (!_preview)
                _state = _play.Decide();
            OnPropertyChanged(string.Empty);
            PlayCommand.NotifyCanExecuteChanged();
            PlayAnywayCommand.NotifyCanExecuteChanged();
        }

        // For review screenshots only (--capture home:fix and the like): PLAY shown in a state the decision gave for
        // made-up inputs, kept until the page closes
        internal void Preview(PlayState state)
        {
            _preview = true;
            _state = state;
            OnPropertyChanged(string.Empty);
        }

        // What the game watcher saw, a few seconds apart
        public async Task WatchGameAsync()
        {
            bool running = await Task.Run(_launcher.IsRunning);
            _play.SeeGame(running);
        }

        private void OnUi(Action action)
        {
            if (_ui == null || SynchronizationContext.Current == _ui)
                action();
            else
                _ui.Post(_ => action(), null);
        }

        public void Dispose()
        {
            _play.Changed -= _onChange;
            _checks.Changed -= _onChange;
            _server.Changed -= _onChange;
        }
    }
}
