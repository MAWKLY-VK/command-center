using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Settings › Graphics' dedicated graphics card switch (ADR 0010): shown on a PC with two graphics cards running
    // Generals Online; turning it on or off runs the change and says so with Undo, and a failed download leaves it off.
    public sealed class DedicatedGpuViewModelTests
    {
        private readonly FakeShell _shell = new();
        private DedicatedGpuState _state = DedicatedGpuState.Off;
        private int _enables, _disables;
        private Exception? _enableFails;

        private static readonly GraphicsCard[] Two =
            [new("Intel(R) UHD Graphics", 0x8086, false), new("NVIDIA GeForce GTX 1650", 0x10DE, false)];

        private DedicatedGpuViewModel Model(IReadOnlyList<GraphicsCard> cards, bool generalsOnline = true) => new(
            cards, () => generalsOnline, () => _state,
            _ =>
            {
                _enables++;
                if (_enableFails != null)
                    return Task.FromException<IReadOnlyList<BackupEntry>>(_enableFails);
                _state = DedicatedGpuState.On;
                return Task.FromResult<IReadOnlyList<BackupEntry>>([]);
            },
            () =>
            {
                _disables++;
                _state = DedicatedGpuState.Off;
                return [];
            },
            new FixRunner(() => { }), _shell);

        [Fact]
        public void It_shows_only_on_a_PC_with_two_cards_that_runs_Generals_Online()
        {
            Assert.True(Model(Two).IsAvailable);
            Assert.Equal("Intel(R) UHD Graphics and NVIDIA GeForce GTX 1650", Model(Two).Cards);
            Assert.False(Model([Two[1]]).IsAvailable);
            Assert.False(Model(Two, generalsOnline: false).IsAvailable);

            // Already in the game folder: shown, so it can be turned off
            _state = DedicatedGpuState.Changed;
            var changed = Model([Two[1]]);
            Assert.True((changed.IsAvailable, changed.IsOn, changed.HasNote) == (true, true, true));
        }

        [Fact]
        public async Task Turning_it_on_and_off_runs_the_change_says_so_and_refreshes_Health()
        {
            var model = Model(Two);
            Assert.False(model.IsOn);

            model.IsOn = true;
            await model.Working;
            Assert.Equal((1, DedicatedGpuState.On, true), (_enables, _state, model.IsOn));
            Assert.Contains(_shell.Toasts, toast => toast.Message.StartsWith("Dedicated graphics card on", StringComparison.Ordinal));

            model.IsOn = false;
            await model.Working;
            Assert.Equal((1, DedicatedGpuState.Off, false), (_disables, _state, model.IsOn));
            Assert.Equal(2, _shell.HealthRefreshes);
        }

        [Fact]
        public async Task A_failed_download_says_why_and_leaves_it_off()
        {
            _enableFails = new InvalidDataException("The download does not match dxwrapper_go's pinned SHA-256, so nothing was changed.");
            var model = Model(Two);
            model.IsOn = true;
            await model.Working;
            Assert.False(model.IsOn);
            Assert.Contains(_shell.Toasts, toast => toast.IsError && toast.Message.Contains("pinned SHA-256", StringComparison.Ordinal));
        }
    }
}
