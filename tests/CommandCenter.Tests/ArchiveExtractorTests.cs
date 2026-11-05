using System.IO.Compression;
using System.Text;

namespace CommandCenter.Tests
{
    // Zips from players and downloads (seam 3), built here. Each refusal comes before anything passes a limit, and the
    // staging folder is gone afterwards; map installs back up every file they replace.
    public sealed class ArchiveExtractorTests : IDisposable
    {
        private readonly TempGame _game = new();
        private readonly string _temp;

        public ArchiveExtractorTests()
        {
            _temp = Path.Combine(_game.Root, "temp");
            Directory.CreateDirectory(_temp);
            DownloadService.TempFolder = _temp;
        }

        private static byte[] Zip(params (string Name, byte[] Data)[] files)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, data) in files)
                {
                    using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    entry.Write(data);
                }
            }
            return stream.ToArray();
        }

        private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

        // Bytes that do not compress
        private static byte[] Noise(int length)
        {
            var data = new byte[length];
            new Random(length).NextBytes(data);
            return data;
        }

        // Text that compresses about ten to one, well inside the ratio limit
        private static byte[] Lines(int count) =>
            Text(string.Concat(Enumerable.Range(0, count).Select(i => $"Waypoint {i} at {i * 7 % 1000}, {i * 13 % 1000}\n")));

        private static string Extract(byte[] zip, ExtractRules rules) => ArchiveExtractor.Extract(new MemoryStream(zip), rules);

        private string[] LeftInTemp() => Directory.EnumerateFileSystemEntries(_temp).ToArray();

        [Fact]
        public void Files_that_add_up_past_the_total_limit_are_stopped_and_nothing_is_left()
        {
            byte[] zip = Zip(("Alpha/A.ini", Lines(4000)), ("Alpha/B.ini", Lines(4000)), ("Alpha/C.ini", Lines(4000)));
            ArchiveExtractor.Limits = ArchiveExtractor.Limits with { Total = 200_000 };
            Assert.True(zip.Length < 200_000);

            var error = Assert.Throws<InvalidDataException>(() => Extract(zip, MapService.ZipRules));
            Assert.Contains("add up to more than", error.Message);
            Assert.Empty(LeftInTemp());
        }

        [Fact]
        public void An_entry_compressed_more_than_200_to_1_is_stopped()
        {
            byte[] zip = Zip(("Alpha/Alpha.map", new byte[3 * 1024 * 1024]));

            var error = Assert.Throws<InvalidDataException>(() => Extract(zip, MapService.ZipRules));
            Assert.Contains("200 to 1", error.Message);
            Assert.Empty(LeftInTemp());
        }

        [Fact]
        public void A_zip_with_more_entries_than_the_limit_is_refused_before_anything_is_extracted()
        {
            byte[] zip = Zip(Enumerable.Range(0, 11).Select(i => ($"Alpha/{i}.txt", Text("x"))).ToArray());
            ArchiveExtractor.Limits = ArchiveExtractor.Limits with { Entries = 10 };

            var error = Assert.Throws<InvalidDataException>(() => Extract(zip, MapService.ZipRules));
            Assert.Contains("more than 10 files", error.Message);
            Assert.Empty(LeftInTemp());
        }

        [Fact]
        public void A_file_larger_than_its_use_allows_is_stopped()
        {
            var rules = MapService.ZipRules with { FileLimit = 1000 };
            Assert.Throws<InvalidDataException>(() => Extract(Zip(("Alpha/Alpha.map", Noise(1001))), rules));
            Assert.Empty(LeftInTemp());
        }

        [Fact]
        public void A_zip_that_declares_less_than_it_holds_never_writes_past_the_limit()
        {
            byte[] zip = Zip(("Alpha/Alpha.map", Noise(5000)));
            // The local header and the central directory both say the file holds 10 bytes
            DeclareSize(zip, 0x04034b50, 22, 10);
            DeclareSize(zip, 0x02014b50, 24, 10);
            var rules = MapService.ZipRules with { FileLimit = 1000 };

            // .NET may stop at the declared size, or the count stops the copy at the limit: either way no file passes it
            string? staging = null;
            try
            {
                staging = Extract(zip, rules);
            }
            catch (InvalidDataException)
            {
                Assert.Empty(LeftInTemp());
            }
            if (staging != null)
            {
                Assert.All(Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories), file => Assert.True(new FileInfo(file).Length <= 1000));
                ArchiveExtractor.Discard(staging);
            }
        }

        private static void DeclareSize(byte[] zip, uint signature, int offset, uint size)
        {
            for (int i = 0; i + 4 <= zip.Length; i++)
            {
                if (BitConverter.ToUInt32(zip, i) == signature)
                {
                    BitConverter.GetBytes(size).CopyTo(zip, i + offset);
                    return;
                }
            }
            Assert.Fail("Header not found.");
        }

        [Theory]
        [InlineData("/Alpha/Alpha.map")]
        [InlineData("../Alpha/Alpha.map")]
        [InlineData("Alpha/../../Alpha.map")]
        [InlineData("C:/Alpha/Alpha.map")]
        [InlineData("C:Alpha.map")]
        [InlineData("//server/share/Alpha.map")]
        [InlineData("Alpha/CON.map")]
        [InlineData("NUL/Alpha.map")]
        [InlineData("Alpha/com1.tga")]
        [InlineData("Alpha/./Alpha.map")]
        [InlineData("Alpha./Alpha.map")]
        [InlineData("...map")]
        [InlineData(".map")]
        public void Names_that_leave_the_folder_or_that_windows_treats_as_something_else_are_refused(string name)
        {
            var error = Assert.Throws<InvalidDataException>(() => Extract(Zip((name, Text("map"))), MapService.ZipRules));
            Assert.Contains(name, error.Message);
            Assert.Empty(LeftInTemp());
        }

        [Fact]
        public void Only_the_files_a_use_accepts_are_extracted()
        {
            byte[] zip = Zip(("Alpha/Alpha.map", Text("map")), ("Alpha/Alpha.tga", Text("tga")), ("Alpha/setup.exe", Text("exe")),
                ("Alpha/.DS_Store", Text("mac")), ("__MACOSX/Alpha/._Alpha.map", Text("mac")));
            string staging = Extract(zip, MapService.ZipRules);
            try
            {
                Assert.Equal([@"Alpha\Alpha.map", @"Alpha\Alpha.tga"],
                    Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(staging, f)).Order().ToArray());
            }
            finally
            {
                ArchiveExtractor.Discard(staging);
            }
            Assert.Empty(LeftInTemp());
        }

        [Fact]
        public void A_control_bar_package_gives_only_its_archives()
        {
            byte[] zip = Zip(("ControlBarProZH/340_ControlBarPro1080ZH.big", Text("big")), ("ControlBarProZH/install.bat", Text("bat")),
                ("readme.txt", Text("txt")));
            string staging = Extract(zip, AddonService.ZipRules);
            try
            {
                Assert.Equal([@"ControlBarProZH\340_ControlBarPro1080ZH.big"],
                    Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(staging, f)).ToArray());
            }
            finally
            {
                ArchiveExtractor.Discard(staging);
            }
        }

        [Fact]
        public void A_map_installs_and_a_reinstall_backs_up_each_replaced_file_with_undo()
        {
            string map = Path.Combine(GamePaths.Maps, @"Alpha\Alpha.map");
            string preview = Path.Combine(GamePaths.Maps, @"Alpha\Alpha.tga");
            string first = _game.Write("first.zip", Zip(("Alpha/Alpha.map", Text("map 1")), ("Alpha/Alpha.tga", Text("preview 1"))));
            Assert.Equal(["Alpha"], MapService.Install(first));
            Assert.Equal("map 1", File.ReadAllText(map));
            Assert.Empty(BackupService.Load());

            string second = _game.Write("second.zip", Zip(("Alpha/Alpha.map", Text("map 2")), ("Alpha/Alpha.tga", Text("preview 2"))));
            Assert.Equal(["Alpha"], MapService.Install(second));
            Assert.Equal("map 2", File.ReadAllText(map));
            var entries = BackupService.Load();
            Assert.Equal(2, entries.Count);
            foreach (var entry in entries)
                Assert.True(BackupService.Undo(entry));
            Assert.Equal("map 1", File.ReadAllText(map));
            Assert.Equal("preview 1", File.ReadAllText(preview));
            Assert.Empty(LeftInTemp());
        }

        [Fact]
        public void A_map_at_the_root_of_a_zip_installs_under_its_own_name()
        {
            string zip = _game.Write("beta.zip", Zip(("Beta.map", Text("map")), ("Beta.tga", Text("preview"))));
            Assert.Equal(["Beta"], MapService.Install(zip));
            Assert.Equal("map", File.ReadAllText(Path.Combine(GamePaths.Maps, @"Beta\Beta.map")));
        }

        [Fact]
        public void A_dot_only_map_at_the_root_of_a_zip_is_refused_and_installs_nothing()
        {
            // In 0.4.0 the map's name became "..", and its files landed beside Options.ini in the game's Documents folder
            string zip = _game.Write("dots.zip", Zip(("...map", Text("map")), ("Options.ini", Text("ours"))));

            var error = Assert.Throws<InvalidDataException>(() => MapService.Install(zip));
            Assert.Contains("...map", error.Message);
            Assert.False(File.Exists(Path.Combine(GamePaths.UserData, "Options.ini")));
            Assert.False(Directory.Exists(GamePaths.Maps) && Directory.EnumerateFileSystemEntries(GamePaths.Maps).Any());
            Assert.Empty(LeftInTemp());
        }

        public void Dispose()
        {
            ArchiveExtractor.Limits = ZipLimits.Default;
            DownloadService.TempFolder = null;
            _game.Dispose();
        }
    }
}
