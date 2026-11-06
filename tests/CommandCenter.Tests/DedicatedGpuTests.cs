using System.IO.Compression;
using System.Security.Cryptography;

namespace CommandCenter.Tests
{
    // The dedicated graphics card (ADR 0010): offered only on a PC with two real graphics cards. dxwrapper_go's two files
    // go into the game folder only when the download and each file match their pinned SHA-256, through BackupService
    // with Undo and never while the game runs; turning it off moves them to quarantine. Health offers it and says when
    // it is on.
    public sealed class DedicatedGpuTests : IDisposable
    {
        private readonly TempGame _game = new();
        private static readonly byte[] Dll = [0x4D, 0x5A, 1, 2, 3];
        private static readonly byte[] Ini = "D3d8to9 = 1\r\nGraphicsHybridAdapter = 1\r\n"u8.ToArray();
        private int _downloads;

        private static GraphicsCard Card(string name, uint vendor, bool software = false) => new(name, vendor, software);
        private static readonly GraphicsCard Intel = Card("Intel(R) UHD Graphics", 0x8086);
        private static readonly GraphicsCard Nvidia = Card("NVIDIA GeForce RTX 3060 Laptop GPU", 0x10DE);

        private static byte[] Zip(params (string Name, byte[] Content)[] files)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in files)
                {
                    using var entry = zip.CreateEntry(name).Open();
                    entry.Write(content);
                }
            }
            return stream.ToArray();
        }

        private static byte[] Package() => Zip((DedicatedGpu.DllName, Dll), (DedicatedGpu.IniName, Ini));

        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        // The download is pinned to pinnedZip's hash (the zip itself unless given), each file to Dll's and Ini's
        private DedicatedGpu Gpu(byte[] zip, byte[]? pinnedZip = null) =>
            new(new WrapperPackage(new Uri("https://example.invalid/dxwrapper_go.zip"), Sha(pinnedZip ?? zip), zip.Length,
                    new Dictionary<string, string> { [DedicatedGpu.DllName] = Sha(Dll), [DedicatedGpu.IniName] = Sha(Ini) }),
                (_, _) =>
                {
                    _downloads++;
                    return Task.FromResult(zip);
                });

        private string InGame(string name) => Path.Combine(_game.Game, name);

        [Fact]
        public void A_laptop_with_a_built_in_and_a_dedicated_card_has_two()
        {
            Assert.True(GraphicsCards.HasTwo([Intel, Nvidia]));
            Assert.True(GraphicsCards.HasTwo([Card("AMD Radeon(TM) Graphics", 0x1002), Card("AMD Radeon RX 6800S", 0x1002)]));
        }

        [Fact]
        public void One_card_with_Windows_own_adapters_is_not_two()
        {
            Assert.False(GraphicsCards.HasTwo([]));
            Assert.False(GraphicsCards.HasTwo([Nvidia]));
            Assert.False(GraphicsCards.HasTwo([Nvidia, Card("Microsoft Basic Render Driver", 0x1414, software: true)]));
            Assert.False(GraphicsCards.HasTwo([Intel, Card("Microsoft Basic Display Adapter", 0x1414)]));
        }

        [Fact]
        public void Reading_this_PCs_cards_does_not_fail()
        {
            Assert.All(GraphicsCards.Read(), card => Assert.False(string.IsNullOrWhiteSpace(card.Name)));
        }

        [Fact]
        public async Task Turning_it_on_copies_only_the_two_pinned_files()
        {
            var gpu = Gpu(Zip((DedicatedGpu.DllName, Dll), (DedicatedGpu.IniName, Ini), ("d3d8.dll", Dll)));
            Assert.Equal(DedicatedGpuState.Off, gpu.State(_game.Game));

            var made = await gpu.EnableAsync(_game.Game);
            Assert.Equal(Dll, File.ReadAllBytes(InGame(DedicatedGpu.DllName)));
            Assert.Equal(Ini, File.ReadAllBytes(InGame(DedicatedGpu.IniName)));
            Assert.False(File.Exists(InGame("d3d8.dll")));
            Assert.Equal(DedicatedGpuState.On, gpu.State(_game.Game));
            Assert.Equal(2, made.Count);
        }

        [Fact]
        public async Task A_download_or_a_file_that_does_not_match_its_pin_changes_nothing()
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Gpu(Package(), pinnedZip: [1, 2, 3]).EnableAsync(_game.Game));
            await Assert.ThrowsAsync<InvalidDataException>(() => Gpu(Zip((DedicatedGpu.DllName, [9, 9]), (DedicatedGpu.IniName, Ini))).EnableAsync(_game.Game));
            await Assert.ThrowsAsync<InvalidDataException>(() => Gpu(Zip((DedicatedGpu.DllName, Dll))).EnableAsync(_game.Game));
            Assert.False(File.Exists(InGame(DedicatedGpu.DllName)));
            Assert.False(File.Exists(InGame(DedicatedGpu.IniName)));
        }

        [Fact]
        public async Task An_ini_the_player_edited_is_backed_up_before_ours_replaces_it()
        {
            string edited = _game.Write(@"game\" + DedicatedGpu.IniName, "D3d8to9 = 1\r\nmy own line\r\n");
            var gpu = Gpu(Package());
            Assert.Equal(DedicatedGpuState.Changed, gpu.State(_game.Game));

            var made = await gpu.EnableAsync(_game.Game);
            Assert.Equal(DedicatedGpuState.On, gpu.State(_game.Game));
            BackupService.Undo(made.Single(entry => entry.Target.EndsWith(DedicatedGpu.IniName, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("my own line", File.ReadAllText(edited));
        }

        [Fact]
        public async Task Turning_it_off_moves_both_files_to_quarantine_and_Undo_brings_them_back()
        {
            var gpu = Gpu(Package());
            await gpu.EnableAsync(_game.Game);

            var moved = gpu.Disable(_game.Game);
            Assert.Equal(2, moved.Count);
            Assert.Equal(DedicatedGpuState.Off, gpu.State(_game.Game));
            foreach (var entry in Enumerable.Reverse(moved))
                BackupService.Undo(entry);
            Assert.Equal(DedicatedGpuState.On, gpu.State(_game.Game));
        }

        [Fact]
        public async Task Nothing_is_downloaded_or_written_while_the_game_runs()
        {
            var gpu = Gpu(Package());
            using (new GameRunning())
                await Assert.ThrowsAsync<InvalidOperationException>(() => gpu.EnableAsync(_game.Game));
            Assert.Equal(0, _downloads);
            Assert.False(File.Exists(InGame(DedicatedGpu.DllName)));
        }

        [Fact]
        public void Health_offers_it_on_a_PC_with_two_cards_and_says_when_it_is_on()
        {
            Func<IProgress<string>?, Task<IReadOnlyList<BackupEntry>>> enable = _ => Task.FromResult<IReadOnlyList<BackupEntry>>([]);

            var offer = HealthService.DedicatedGpuCheck([Intel, Nvidia], DedicatedGpuState.Off, enable)!;
            Assert.Equal((HealthStatus.Warning, "Enable dedicated graphics card"), (offer.Status, offer.FixLabel));
            Assert.Same(enable, offer.FixAsync);
            Assert.Contains(Nvidia.Name, offer.Detail);

            Assert.Null(HealthService.DedicatedGpuCheck([Nvidia], DedicatedGpuState.Off, enable));
            var on = HealthService.DedicatedGpuCheck([Nvidia], DedicatedGpuState.On, enable)!;
            Assert.Equal((HealthStatus.Passed, "Dedicated graphics card is on"), (on.Status, on.Title));
            var changed = HealthService.DedicatedGpuCheck([Intel, Nvidia], DedicatedGpuState.Changed, enable)!;
            Assert.Equal(HealthStatus.Passed, changed.Status);
            Assert.Null(changed.FixAsync);
        }

        public void Dispose() => _game.Dispose();
    }
}
