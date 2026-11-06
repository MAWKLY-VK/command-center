using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommandCenter.Pages;

namespace CommandCenter.ViewModels
{
    // The Settings page: Command Center's own sections (ticket 08: the game folder, the launcher, Appearance and Add-ons),
    // which apply at once, and Generals Online's (ticket 09), which are read when Settings opens and written when it is
    // left. The services come from AppServices; tests pass stand-ins.
    public sealed class SettingsViewModel(GameFolderViewModel gameFolder, LauncherViewModel launcher, AppearanceViewModel appearance,
        AddonsViewModel addons, GameSettingsViewModel game, NetworkDiagnosticsViewModel diagnostics, PluginsViewModel plugins) : ISettingsSection
    {
        public GameFolderViewModel GameFolder { get; } = gameFolder;
        public LauncherViewModel Launcher { get; } = launcher;
        public AppearanceViewModel Appearance { get; } = appearance;
        public AddonsViewModel Addons { get; } = addons;
        public GameSettingsViewModel Game { get; } = game;
        public NetworkDiagnosticsViewModel Diagnostics { get; } = diagnostics;
        public PluginsViewModel Plugins { get; } = plugins;

        public void Load()
        {
            Game.Load();
            Plugins.Refresh();
        }

        public void Commit() => Game.Commit();
    }

    // Which Zero Hour is played, where that came from, what is in it, and where the game keeps its data
    public sealed partial class GameFolderViewModel : ObservableObject
    {
        private readonly IGameFolder _game;
        private readonly IShell _shell;
        private readonly Func<string> _clientVersion;
        private readonly Func<string> _dataFolder;

        public GameFolderViewModel(IGameFolder game, IShell shell, Func<string> clientVersion, Func<string> dataFolder)
        {
            _game = game;
            _shell = shell;
            _clientVersion = clientVersion;
            _dataFolder = dataFolder;
            game.Changed += () => OnPropertyChanged(string.Empty);
        }

        public bool IsFound => _game.ZeroHourFound;
        public string Folder => _game.ZeroHourFound ? _game.Path : "Game not found";

        public string Source => _game.Source switch
        {
            GameFolderSource.NotFound => "Not found",
            GameFolderSource.ChosenByPlayer => "Chosen by you",
            GameFolderSource.Steam => "Steam copy",
            _ => "Other copy",
        };

        // What the folder holds, and what it lacks
        public IReadOnlyList<string> Found => !_game.ZeroHourFound ? []
            : _game.GeneralsOnlineFound ? ["Zero Hour found", "Generals Online " + _clientVersion() + " installed"]
            : ["Zero Hour found"];

        public IReadOnlyList<string> Missing => _game.ZeroHourFound && !_game.GeneralsOnlineFound ? ["Generals Online is not installed"] : [];

        public string Note => !_game.ZeroHourFound ? "Choose the folder of any copy of Zero Hour: Steam, EA or another install."
            : _game.GeneralsOnlineFound ? "Any copy works: Steam, EA or another install. Changing the folder changes nothing in the game files."
            : "Install Generals Online into this folder, then press Play.";

        public string DataFolder => _dataFolder();

        // The player picks the Zero Hour folder; it is remembered, and whatever depends on the game hears of it
        [RelayCommand]
        private void ChangeFolder()
        {
            if (_shell.ChooseFolder("Choose the Zero Hour folder", _game.ZeroHourFound ? _game.Path : null) is not { } folder)
                return;
            if (!_game.Choose(folder))
            {
                _shell.Warn("That folder does not hold Zero Hour. Choose the folder with WindowZH.big and INIZH.big in it.");
                return;
            }
            _shell.Toast("Game folder changed. The checks run again.");
        }

        [RelayCommand]
        private void OpenDataFolder() => Views.ShowInFolder(DataFolder);
    }

    // Command Center's own version and the update check
    public sealed partial class LauncherViewModel(IShell shell, string version) : ObservableObject
    {
        public string Version { get; } = "Command Center " + version;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowUpdateStatus))]
        private string _updateStatus = "";

        public bool ShowUpdateStatus => UpdateStatus.Length > 0;

        // An update opens the update screen; otherwise the line says this is the latest version, or why the check
        // could not run
        [RelayCommand]
        private async Task CheckForUpdatesAsync()
        {
            UpdateStatus = "Checking…";
            var check = await shell.CheckForUpdateAsync();
            UpdateStatus = check.Update != null ? "" : check.Failure ?? "You have the latest version.";
        }
    }

    // One choice in a set, for radio buttons: selecting it writes the setting
    public sealed partial class Choice<T>(T value, string label, Func<T> current, Action<T> choose) : ObservableObject
    {
        public T Value { get; } = value;
        public string Label { get; } = label;

        public bool Selected
        {
            get => EqualityComparer<T>.Default.Equals(current(), Value);
            set
            {
                if (value)
                    choose(Value);
            }
        }

        public void Refresh() => OnPropertyChanged(nameof(Selected));
    }

    // Home's picture, animation effects and interface sounds
    public sealed partial class AppearanceViewModel : ObservableObject
    {
        private readonly IAppearance _appearance;

        public AppearanceViewModel(IAppearance appearance)
        {
            _appearance = appearance;
            Pictures =
            [
                Picture(HomeImage.Changing, "Changes at each start"),
                Picture(HomeImage.MainMenu, "Battle scene"),
                Picture(HomeImage.Usa, "USA"),
                Picture(HomeImage.China, "China"),
                Picture(HomeImage.Gla, "GLA"),
            ];
            Animations =
            [
                Animation(AnimationSetting.FollowWindows, "Follow Windows"),
                Animation(AnimationSetting.On, "On"),
                Animation(AnimationSetting.Off, "Off"),
            ];
        }

        public IReadOnlyList<Choice<HomeImage>> Pictures { get; }
        public IReadOnlyList<Choice<AnimationSetting>> Animations { get; }

        public bool InterfaceSounds
        {
            get => _appearance.InterfaceSounds;
            set
            {
                _appearance.InterfaceSounds = value;
                OnPropertyChanged();
            }
        }

        private Choice<HomeImage> Picture(HomeImage value, string label) => new(value, label, () => _appearance.HomeImage, chosen =>
        {
            _appearance.HomeImage = chosen;
            foreach (var picture in Pictures)
                picture.Refresh();
        });

        private Choice<AnimationSetting> Animation(AnimationSetting value, string label) => new(value, label, () => _appearance.Animations, chosen =>
        {
            _appearance.Animations = chosen;
            foreach (var animation in Animations)
                animation.Refresh();
        });
    }
}
