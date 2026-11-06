using CommunityToolkit.Mvvm.ComponentModel;

namespace CommandCenter.ViewModels
{
    // Settings › Graphics: the dedicated graphics card (ADR 0010). It shows on a PC with two graphics cards that runs
    // Generals Online, and wherever dxwrapper_go is already in the game folder, so it can be turned off. The switch shows
    // what is in the game folder; turning it on downloads and copies dxwrapper_go, turning it off moves it to quarantine,
    // each said in a message with Undo. Health is checked again after each change.
    public sealed partial class DedicatedGpuViewModel : ObservableObject
    {
        private readonly IReadOnlyList<GraphicsCard> _cards;
        private readonly Func<bool> _generalsOnline;
        private readonly Func<DedicatedGpuState> _state;
        private readonly Func<IProgress<string>?, Task<IReadOnlyList<BackupEntry>>> _enable;
        private readonly Func<IReadOnlyList<BackupEntry>> _disable;
        private readonly FixRunner _fixes;
        private readonly IShell _shell;
        private bool _showing;

        public DedicatedGpuViewModel(IReadOnlyList<GraphicsCard> cards, Func<bool> generalsOnline, Func<DedicatedGpuState> state,
            Func<IProgress<string>?, Task<IReadOnlyList<BackupEntry>>> enable, Func<IReadOnlyList<BackupEntry>> disable, FixRunner fixes, IShell shell)
        {
            _cards = cards;
            _generalsOnline = generalsOnline;
            _state = state;
            _enable = enable;
            _disable = disable;
            _fixes = fixes;
            _shell = shell;
            Refresh();
        }

        public DedicatedGpuState State { get; private set; }

        public bool IsAvailable => _generalsOnline() && (GraphicsCards.HasTwo(_cards) || State != DedicatedGpuState.Off);

        // The PC's graphics cards, by name
        public string Cards => English.List(GraphicsCards.Real(_cards).Select(card => card.Name));

        // dxwrapper_go is there but differs from Command Center's copy
        public bool HasNote => State == DedicatedGpuState.Changed;

        // The change in progress, for tests to wait on
        public Task Working { get; private set; } = Task.CompletedTask;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        private bool _isBusy;

        public bool IsIdle => !IsBusy;

        [ObservableProperty]
        private bool _isOn;

        // What is in the game folder now (Settings opened, or a change finished)
        public void Refresh()
        {
            State = _state();
            _showing = true;
            IsOn = State != DedicatedGpuState.Off;
            _showing = false;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(HasNote));
        }

        partial void OnIsOnChanged(bool value)
        {
            if (_showing || IsBusy)
                return;
            Working = value ? TurnOnAsync() : TurnOff();
        }

        private async Task TurnOnAsync()
        {
            IsBusy = true;
            try
            {
                var made = await _enable(new Progress<string>(text => _shell.Toast(text)));
                Said("Dedicated graphics card on. Generals Online uses it from its next start.", made);
            }
            catch (Exception ex)
            {
                _shell.Toast(string.Format("Could not turn on the dedicated graphics card: {0}", ex.Message), isError: true);
            }
            finally
            {
                IsBusy = false;
                Changed();
            }
        }

        private Task TurnOff()
        {
            try
            {
                Said("Dedicated graphics card off. dxwrapper_go is in quarantine.", _disable());
            }
            catch (Exception ex)
            {
                _shell.Toast(string.Format("Could not turn off the dedicated graphics card: {0}", ex.Message), isError: true);
            }
            Changed();
            return Task.CompletedTask;
        }

        private void Said(string message, IReadOnlyList<BackupEntry> made)
        {
            var outcome = new FixOutcome([], [], made, OnlySamples: false);
            _shell.Toast(message, outcome.CanUndo ? () =>
            {
                string? failure = _fixes.Undo(outcome);
                _shell.Toast(failure ?? "Change undone", isError: failure != null);
                Changed();
            } : null);
        }

        private void Changed()
        {
            Refresh();
            _shell.RefreshHealth();
        }
    }
}
