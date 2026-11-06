using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommandCenter.Pages;

namespace CommandCenter.ViewModels
{
    public sealed record ResolutionOption(string Value, string Label);

    // Add-ons in Settings (ticket 08): the control bar, the game's own or Control Bar Pro in five sizes, changed and undone
    // through AddonService. Moved out of the section's code-behind with its texts and rules unchanged.
    public sealed partial class AddonsViewModel : ObservableObject
    {
        private readonly IAddons _addons;
        private readonly IGameFolder _game;
        private readonly IShell _shell;

        private (BarKind Kind, string? Resolution) _installed;
        private bool _goManaged, _busy, _touched;
        // Other stands for neither choice, while the installed bar is not one offered here
        private BarKind _chosen = BarKind.Other;
        private string? _resolution;
        private List<string> _otherFiles = [];

        public AddonsViewModel(IAddons addons, IGameFolder game, IShell shell)
        {
            _addons = addons;
            _game = game;
            _shell = shell;
            Resolutions = addons.Packages.Where(p => p.Kind == BarKind.Pro)
                .Select(p => new ResolutionOption(p.Resolution, p.Resolution.Replace("x", " × ")))
                .ToList();
            Refresh();
        }

        public IReadOnlyList<ResolutionOption> Resolutions { get; }

        public bool OriginalChosen
        {
            get => _chosen == BarKind.Original;
            set
            {
                if (value)
                    Choose(BarKind.Original);
            }
        }

        public bool ProChosen
        {
            get => _chosen == BarKind.Pro;
            set
            {
                if (value)
                    Choose(BarKind.Pro);
            }
        }

        // Picking a size chooses Control Bar Pro
        public string? ChosenResolution
        {
            get => _resolution;
            set
            {
                if (value == null || value == _resolution)
                    return;
                _resolution = value;
                _touched = true;
                _chosen = BarKind.Pro;
                Changed();
            }
        }

        public bool OriginalInstalled => _installed.Kind == BarKind.Original;
        public bool ProInstalled => _installed.Kind == BarKind.Pro;
        public bool CanChoose => !_goManaged && !_busy;

        public bool ShowOtherNotice => _installed.Kind == BarKind.Other || _goManaged;
        public string OtherTitle => _goManaged ? "Installed by the Generals Online launcher" : "Another control bar is installed";
        public string OtherDetail =>
            _goManaged ? "Change or remove it in the Generals Online launcher. Command Center does not touch files that launcher manages, so the two never undo each other's changes."
            : _installed.Kind == BarKind.Other ? string.Format("{0} in the game folder. Choose the original bar or Control Bar Pro to replace it; Undo puts it back.", string.Join(", ", _otherFiles))
            : "";

        public bool ShowCompare => _chosen == BarKind.Pro;
        public string CompareNote => ShowCompare
            ? "Drag across the picture to compare. Pick the size that matches your game resolution; a wrong size looks stretched."
            : "Choose Control Bar Pro to compare it with the original.";

        public bool HasPendingChanges => Pending;
        public bool CanApply => Pending && !_busy;
        public bool CanDiscard => Pending && !_busy;

        public string ChangeText { get; private set; } = "";
        public string ChangeDetail { get; private set; } = "";

        private string? PendingResolution => _chosen == BarKind.Pro ? _resolution : null;

        private bool Pending => _game.ZeroHourFound && !_goManaged
                                && (_chosen != _installed.Kind || (_chosen == BarKind.Pro && PendingResolution != _installed.Resolution));

        // Reads which bar is in the game folder; a choice the player made and has not applied yet stays
        public void Refresh()
        {
            try
            {
                _installed = _game.ZeroHourFound ? _addons.Installed() : (BarKind.Original, null);
            }
            catch
            {
                _installed = (BarKind.Original, null);
            }
            try
            {
                _goManaged = _game.ZeroHourFound && _addons.ManagedByGo();
            }
            catch
            {
                _goManaged = false;
            }
            try
            {
                _otherFiles = !_goManaged && _installed.Kind == BarKind.Other
                    ? _addons.InstalledFiles().Select(Path.GetFileName).OfType<string>().ToList()
                    : [];
            }
            catch
            {
                _otherFiles = [];
            }
            if (!_touched || !Pending)
                SelectInstalled();
            Changed();
        }

        [RelayCommand]
        private void Discard()
        {
            SelectInstalled();
            Changed();
        }

        [RelayCommand]
        private async Task ApplyAsync()
        {
            if (_busy || !Pending)
                return;
            var kind = _chosen;
            string? resolution = PendingResolution;
            _busy = true;
            ChangeText = BarName(kind, resolution);
            OnPropertyChanged(string.Empty);
            var progress = new Progress<string>(text =>
            {
                ChangeDetail = text;
                OnPropertyChanged(nameof(ChangeDetail));
            });
            try
            {
                var made = await _addons.ApplyAsync(kind, resolution, progress);
                _touched = false;
                _shell.Toast("Control bar changed. It shows the next time the game starts.", made.Count == 0 ? null : () => UndoChange(made));
            }
            catch (Exception ex)
            {
                _shell.Toast(ex.Message, isError: true);
            }
            finally
            {
                _busy = false;
                Refresh();
                _shell.RefreshHealth();
            }
        }

        private void UndoChange(List<BackupEntry> made)
        {
            try
            {
                _addons.Undo(made);
                _shell.Toast("Previous control bar put back");
            }
            catch (Exception ex)
            {
                _shell.Toast(string.Format("Something went wrong: {0}", ex.Message), isError: true);
            }
            _touched = false;
            Refresh();
            _shell.RefreshHealth();
        }

        private void Choose(BarKind kind)
        {
            if (_chosen == kind)
                return;
            _chosen = kind;
            _touched = true;
            Changed();
        }

        // A bar that is not offered here leaves both choices empty until one is picked
        private void SelectInstalled()
        {
            _chosen = _installed.Kind;
            string resolution;
            try
            {
                resolution = _installed.Resolution ?? _addons.SuggestedResolution();
            }
            catch
            {
                resolution = "1920x1080";
            }
            _resolution = Resolutions.Any(r => r.Value == resolution) ? resolution : Resolutions.ElementAtOrDefault(2)?.Value;
            _touched = false;
        }

        private void Changed()
        {
            ShowChange();
            OnPropertyChanged(string.Empty);
        }

        private void ShowChange()
        {
            if (!_game.ZeroHourFound)
            {
                ChangeText = "Folder not found";
                ChangeDetail = "Zero Hour was not found. Press Play on the home page to choose the game folder.";
                return;
            }
            // While a change runs, its own progress shows
            if (_busy)
                return;
            if (Pending)
            {
                ChangeText = BarName(_installed.Kind, _installed.Resolution) + "  →  " + BarName(_chosen, PendingResolution);
                var package = _addons.Package(_chosen, PendingResolution);
                ChangeDetail = package == null
                    ? "Moves the add-on archives out of the game folder; Undo puts them back."
                    : string.Format("Downloads {0} from GitHub, checks it, then adds the .big files to the game folder.", Views.Size(package.Size));
            }
            else
            {
                ChangeText = string.Format("Installed now: {0}", BarName(_installed.Kind, _installed.Resolution));
                ChangeDetail = _goManaged
                    ? "Managed by the Generals Online launcher."
                    : "Choose another bar to change it. The new bar shows the next time the game starts.";
            }
        }

        private static string BarName(BarKind kind, string? resolution) => kind switch
        {
            BarKind.Pro => resolution != null ? "Control Bar Pro " + resolution.Replace("x", " × ") : "Control Bar Pro",
            BarKind.Other => "Another control bar",
            _ => "Original bar",
        };
    }
}
