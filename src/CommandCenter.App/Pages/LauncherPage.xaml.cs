using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommandCenter.ViewModels;

namespace CommandCenter.Pages
{
    // Home, the Briefing stage (ticket 13): the view of HomeViewModel. The page builds the models from the shared
    // services, watches for the game every few seconds and asks for the server numbers every minute while it shows.
    public partial class LauncherPage : Page
    {
        private readonly HomeViewModel _model;
        private readonly bool _still = App.HasArg("--capture");
        private readonly DispatcherTimer _gameWatch = new() { Interval = TimeSpan.FromSeconds(3) };
        private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMinutes(1) };
        private bool _loadedOnce;

        public LauncherPage()
        {
            InitializeComponent();
            var shell = Views.Main;
            var play = new PlayFlow(AppServices.Launcher, AppServices.GameFolder, AppServices.Health, AppServices.Server, AppServices.Fixes, shell,
                QuickChecksAsync, Views.Open, AfterGame);
            var lastMatch = new LastMatchViewModel(() => AppState.Replays, ReplayService.ReadResults,
                replay => Tools.ReplayRow.MapName(replay, MapCatalog.InstalledFolders()), replay => Views.MapPreview(replay.MapPath, replay.Map),
                ReplayService.Watch, AppServices.Launcher.IsRunning, shell);
            _model = new HomeViewModel(play, AppServices.Health, AppServices.Server, AppServices.Launcher, lastMatch, shell, AppState.ClientVersion,
                () => AppState.MatchChanges, () => LauncherJson.Load().PreferLive, SavePreferLive);
            DataContext = _model;

            AppState.ReplaysChanged += () => Dispatcher.Invoke(() => _ = lastMatch.ShowAsync());
            AppState.MatchGuardChanged += () => Dispatcher.Invoke(_model.RefreshGuard);
            // Another profile or game folder can name other programs
            GoProfile.Changed += () => Dispatcher.Invoke(_model.Refresh);
            AppServices.GameFolder.Changed += () => Dispatcher.Invoke(_model.Refresh);

            _gameWatch.Tick += async (_, _) => await _model.WatchGameAsync();
            _statsTimer.Tick += (_, _) => _ = AppServices.Server.RefreshAsync();
            Loaded += OnLoaded;
            Unloaded += (_, _) =>
            {
                _gameWatch.Stop();
                _statsTimer.Stop();
            };
        }

        // For review screenshots: PLAY in one of its states (home:locate, home:update, home:fix or home:play), as the
        // decision gives it for made-up inputs. Other names (the pictures) change nothing.
        public void PreviewPlay(string state)
        {
            var server = AppServices.Server;
            PlayInputs? inputs = state.ToLowerInvariant() switch
            {
                "locate" => new PlayInputs(false, [], server.Stats, server.Asked),
                "update" => new PlayInputs(true, [new HealthResult
                {
                    Id = HealthService.GoVersionId, Group = HealthService.GroupNetwork, Title = "Generals Online has a new version", Detail = "",
                    Status = HealthStatus.Warning,
                }], server.Stats, server.Asked),
                "fix" => new PlayInputs(true, [new HealthResult
                {
                    Id = "sample:runtime", Group = HealthService.GroupFiles, Title = "Old Visual C++ file inside the game folder", Detail = "",
                    Status = HealthStatus.Problem, Impact = HealthImpact.StopsGame, IsSample = true,
                }], server.Stats, server.Asked),
                "play" => new PlayInputs(true, [], server.Stats, server.Asked),
                _ => null,
            };
            if (inputs != null)
                _model.Preview(PlayDecision.Decide(inputs));
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _gameWatch.Start();
            _ = _model.WatchGameAsync();
            if (!_still)
                _statsTimer.Start();
            if (_loadedOnce)
            {
                _model.Refresh();
                return;
            }
            _loadedOnce = true;

            try
            {
                Directory.CreateDirectory(GamePaths.GoData);
            }
            catch
            {
                // Generals Online creates it itself on its first start
            }
            _ = _model.LastMatch.ShowAsync();
            if (AppState.Stats == null)
                _ = AppServices.Server.RefreshAsync();
            if (AppState.HealthCheckedAt == null)
                _ = AppServices.Health.CheckAsync();
            if (AppState.Replays.Count == 0)
                _ = AppState.RefreshReplaysAsync();
            if (!AppState.MatchGuardChecked)
                _ = AppState.RefreshMatchGuardAsync();
        }

        // The fast checks of the player's own files, just before the launch (no network)
        private static async Task<IReadOnlyList<HealthResult>> QuickChecksAsync() => await Task.Run(HealthService.Run);

        // A game closed: its replay and what the match guard keeps may have changed
        private static void AfterGame()
        {
            _ = AppState.RefreshReplaysAsync();
            _ = AppState.RefreshMatchGuardAsync();
        }

        // launcher.json is edited in place, so the fields Command Center does not know stay
        private static void SavePreferLive(bool live)
        {
            var launcher = LauncherJson.Load();
            launcher.PreferLive = live;
            launcher.Save(live ? "Chose the Live client" : "Chose the Test client");
        }
    }
}
