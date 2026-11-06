using System.Windows.Media;
using CommandCenter.Pages;
using CommandCenter.Pages.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommandCenter.ViewModels
{
    // Home's Last match panel (ticket 13): the newest replay the game recorded, with its map, when it was played, its
    // length and mode, and the recorder's result once it is read from the file (off the UI thread). Watch plays it in the
    // game, so it waits until the game is closed.
    public sealed partial class LastMatchViewModel : ObservableObject
    {
        private readonly Func<IReadOnlyList<ReplayEntry>> _replays;
        private readonly Func<ReplayEntry, ReplayResults?> _readResults;
        private readonly Func<ReplayEntry, string> _mapName;
        private readonly Func<ReplayEntry, ImageSource?> _preview;
        private readonly Action<ReplayEntry> _watch;
        private readonly Func<bool> _gameRunning;
        private readonly IShell _shell;
        private ReplayEntry? _match;
        private int _version;

        public LastMatchViewModel(Func<IReadOnlyList<ReplayEntry>> replays, Func<ReplayEntry, ReplayResults?> readResults, Func<ReplayEntry, string> mapName,
            Func<ReplayEntry, ImageSource?> preview, Action<ReplayEntry> watch, Func<bool> gameRunning, IShell shell)
        {
            _replays = replays;
            _readResults = readResults;
            _mapName = mapName;
            _preview = preview;
            _watch = watch;
            _gameRunning = gameRunning;
            _shell = shell;
        }

        public bool HasMatch => _match != null;
        public bool NoMatch => _match == null;
        public string Map { get; private set; } = "";
        public string Info { get; private set; } = "";
        public ImageSource? Preview { get; private set; }

        // A map without a preview in the game shows no picture rather than an empty box
        public bool HasPreview => Preview != null;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasResult))]
        private string? _result;

        public bool HasResult => Result != null;

        // The newest replay at once; its result when the file has been read, unless a newer one showed meanwhile
        public async Task ShowAsync()
        {
            int version = ++_version;
            _match = _replays().OrderByDescending(r => r.Start).FirstOrDefault();
            Result = null;
            if (_match is not { } match)
            {
                OnPropertyChanged(string.Empty);
                return;
            }
            string map = _mapName(match);
            Map = map.Length > 0 ? map : "Unknown map";
            Info = string.Join(", ", Views.Day(match.Start), Views.Length(match.Length), ReplayRow.ModeText(match.Mode));
            Preview = _preview(match);
            OnPropertyChanged(string.Empty);

            ReplayResults? results;
            try
            {
                results = await Task.Run(() => _readResults(match));
            }
            catch
            {
                // A damaged replay shows without its result
                return;
            }
            if (version != _version || results == null || results.Recorder < 0 || results.Recorder >= results.Players.Count)
                return;
            Result = results.Players[results.Recorder].Result switch
            {
                MatchResult.Won => "Won",
                MatchResult.Lost => "Lost",
                _ => null,
            };
        }

        [RelayCommand]
        private void Watch()
        {
            if (_match == null)
                return;
            if (_gameRunning())
            {
                _shell.Toast("Close the game first; a replay plays in the game itself.");
                return;
            }
            try
            {
                _watch(_match);
            }
            catch (Exception ex)
            {
                _shell.Toast(ex.Message, isError: true);
            }
        }
    }
}
