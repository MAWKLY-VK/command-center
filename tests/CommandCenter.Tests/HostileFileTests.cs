using System.Text;

namespace CommandCenter.Tests
{
    // Damaged or hostile files through the readers' public entry points (seam 6). Each is refused before any large
    // allocation, and a file that is skipped leaves one line in the error log.
    public sealed class HostileFileTests : IDisposable
    {
        // The most a refusal may allocate; the files below claim between 16 MB and 4 GB
        private const long Small = 1024 * 1024;

        private readonly TempGame _game = new();

        private static long Allocated(Action read)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            read();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private static string[] LogLines() =>
            File.Exists(ErrorLog.FilePath) ? File.ReadAllLines(ErrorLog.FilePath).Where(l => l.Length > 0).ToArray() : [];

        private static byte[] Int(int value) => BitConverter.GetBytes(value);

        // A run-length-encoded 32-bit TGA of one colour: packets of up to 128 pixels, 5 bytes each
        private static byte[] PlainRleTga(int width, int height)
        {
            var body = new List<byte>();
            for (int left = width * height; left > 0; left -= 128)
            {
                body.Add((byte)(0x80 | (Math.Min(128, left) - 1)));
                body.AddRange([10, 20, 30, 255]);
            }
            return Images.Tga(width, height, 32, topDown: true, rle: true, [.. body]);
        }

        [Fact]
        public void A_map_preview_wider_than_1024_pixels_is_skipped_and_logged_once()
        {
            string preview = _game.Write(@"user\Maps\Wide\Wide.tga", PlainRleTga(1100, 8));
            Assert.Null(TgaImage.Load(preview));
            Assert.Null(TgaImage.Load(preview));
            Assert.Single(LogLines(), line => line.Contains(preview));

            // The same picture is within the limit for the game's textures
            Assert.NotNull(TgaImage.Decode(File.ReadAllBytes(preview)));
        }

        [Fact]
        public void A_tga_whose_size_is_beyond_the_limit_or_its_data_is_refused_without_a_large_allocation()
        {
            byte[][] pictures =
            [
                Images.Tga(65535, 65535, 32, topDown: true, rle: false, new byte[64]),
                Images.Tga(4000, 4000, 32, topDown: true, rle: false, new byte[1024]),
                Images.Tga(4000, 4000, 32, topDown: true, rle: true, [0xFF, 1, 2, 3, 4]),
            ];
            foreach (byte[] picture in pictures)
            {
                DecodedImage? image = new(0, 0, []);
                Assert.True(Allocated(() => image = TgaImage.Decode(picture)) < Small);
                Assert.Null(image);
            }
        }

        [Fact]
        public void A_dds_whose_size_is_beyond_the_limit_or_its_data_is_refused_without_a_large_allocation()
        {
            byte[][] textures =
            [
                Images.Dds("DXT1", 8192, 4, new byte[8 * 2048]),
                Images.Dds("DXT5", 4096, 4096, new byte[1024]),
                Images.Dds("DXT1", -4, 4, new byte[8]),
            ];
            foreach (byte[] texture in textures)
            {
                DecodedImage? image = new(0, 0, []);
                Assert.True(Allocated(() => image = Dds.Decode(texture)) < Small);
                Assert.Null(image);
            }
            Assert.True(Allocated(() => Assert.Throws<InvalidDataException>(() =>
                Dds.DecodePixels(textures[1], "DXT5", 4096, 4096, Dds.HeaderSize))) < Small);
        }

        // A compressed map: "EAR\0", the size, then a RefPack stream that declares its size in four bytes
        private static byte[] RefPackMap(uint declared, params byte[] commands) =>
            [.. "EAR\0"u8, .. BitConverter.GetBytes(declared), 0x90, 0xFB,
             (byte)(declared >> 24), (byte)(declared >> 16), (byte)(declared >> 8), (byte)declared, .. commands];

        [Fact]
        public void A_map_stream_that_declares_more_than_64_mb_or_more_than_it_can_produce_is_refused_without_a_large_allocation()
        {
            byte[][] maps =
            [
                RefPackMap(100 * 1024 * 1024, 0xFC),
                RefPackMap(32 * 1024 * 1024, 0xFC),
                RefPackMap(16, 0x00),
            ];
            foreach (byte[] map in maps)
                Assert.True(Allocated(() => Assert.Throws<InvalidDataException>(() => RefPack.Decompress(map))) < Small);
        }

        [Fact]
        public void A_damaged_map_counts_no_players_and_is_logged()
        {
            string map = _game.Write(@"user\Maps\Broken\Broken.map", RefPackMap(32 * 1024 * 1024, 0xFC));
            Assert.Equal(0, MapService.CountPlayers(map));
            Assert.Single(LogLines(), line => line.Contains(map));
        }

        // A .big with these index entries (offset and size given, so they can point anywhere) and these bytes after
        // the index
        private static void WriteBig(string path, (string Name, uint Offset, uint Size)[] entries, byte[] data, uint count = 0)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            void BigEndian(uint value) => w.Write(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
            w.Write("BIGF"u8);
            w.Write(0);
            BigEndian(count == 0 ? (uint)entries.Length : count);
            BigEndian(0);
            foreach (var (name, offset, size) in entries)
            {
                BigEndian(offset);
                BigEndian(size);
                w.Write(Encoding.ASCII.GetBytes(name));
                w.Write((byte)0);
            }
            w.Write(data);
            w.Flush();
            File.WriteAllBytes(path, stream.ToArray());
        }

        private static int IndexSize(params string[] names) => 16 + names.Sum(n => 8 + n.Length + 1);

        [Fact]
        public void Archive_entries_that_lie_outside_the_archive_are_ignored_and_logged()
        {
            string archive = Path.Combine(_game.Game, "Hostile.big");
            string[] names = [@"Data\INI\Good.ini", @"Data\INI\Far.ini", @"Data\INI\Long.ini"];
            uint first = (uint)IndexSize(names);
            WriteBig(archive, [(names[0], first, 4), (names[1], 0x7FFFFFF0, 16), (names[2], first, 0xFFFFFFF0)], "good"u8.ToArray());

            var files = new GameFiles(_game.Game, communityPatch: false);
            Assert.Equal("good", files.ReadText(names[0]));
            Assert.False(files.Exists(names[1]));
            Assert.False(files.Exists(names[2]));
            Assert.Single(LogLines(), line => line.Contains(archive));
        }

        [Fact]
        public void An_archive_index_that_runs_on_keeps_its_good_entries_and_is_logged()
        {
            string archive = Path.Combine(_game.Game, "Endless.big");
            string name = @"Data\INI\Good.ini";
            uint first = (uint)IndexSize(name);
            // Four billion entries are claimed; after the first one the index is a name with no end
            WriteBig(archive, [(name, first, 4)], [.. "good"u8, .. Enumerable.Repeat((byte)'A', 1_200_000)], count: uint.MaxValue);

            GameFiles? files = null;
            Assert.True(Allocated(() => files = new GameFiles(_game.Game, communityPatch: false)) < Small);
            Assert.Equal("good", files!.ReadText(name));
            Assert.Single(LogLines(), line => line.Contains(archive));
        }

        [Fact]
        public void An_archive_index_with_more_entries_than_the_limit_keeps_only_the_first_ones_and_is_logged()
        {
            string archive = Path.Combine(_game.Game, "Crowded.big");
            int count = GameFiles.MaxEntries + 5;
            var names = Enumerable.Range(0, count).Select(i => $"F{i}").ToArray();
            uint first = (uint)IndexSize(names);
            WriteBig(archive, names.Select(n => (n, first, 1u)).ToArray(), [0x41]);

            var files = new GameFiles(_game.Game, communityPatch: false);
            Assert.True(files.Exists("F0"));
            Assert.False(files.Exists($"F{GameFiles.MaxEntries}"));
            Assert.Single(LogLines(), line => line.Contains(archive));
        }

        [Fact]
        public void A_file_larger_than_the_read_limit_is_not_read()
        {
            string archive = Path.Combine(_game.Game, "Large.big");
            string name = @"Art\Textures\Large.tga";
            uint first = (uint)IndexSize(name);
            uint size = GameFiles.ReadLimit + 1;
            WriteBig(archive, [(name, first, size)], []);
            using (var file = new FileStream(archive, FileMode.Open))
                file.SetLength(first + size);

            var files = new GameFiles(_game.Game, communityPatch: false);
            Assert.True(files.Exists(name));
            byte[]? data = [];
            Assert.True(Allocated(() => data = files.Read(name)) < Small);
            Assert.Null(data);
            Assert.Single(LogLines(), line => line.Contains(name));
        }

        // The string table's header: " FSC", version, label count, string count, two unused values
        private static byte[] Csf(int labels, params byte[] body) =>
            [.. " FSC"u8, .. Int(3), .. Int(labels), .. Int(labels), .. Int(0), .. Int(0), .. body];

        [Fact]
        public void A_string_table_whose_counts_or_lengths_run_past_its_data_is_damaged_without_a_large_allocation()
        {
            byte[][] tables =
            [
                Csf(int.MaxValue),
                Csf(1, [.. " LBL"u8, .. Int(1), .. Int(50_000_000), .. "A"u8]),
                Csf(1, [.. " LBL"u8, .. Int(1), .. Int(1), .. "A"u8, .. " RTS"u8, .. Int(30_000_000)]),
                Csf(1, [.. " LBL"u8, .. Int(1), .. Int(1), .. "A"u8, .. "WRTS"u8, .. Int(0), .. Int(50_000_000)]),
            ];
            foreach (byte[] table in tables)
                Assert.True(Allocated(() => Assert.Throws<InvalidDataException>(() => CsfFile.Load(table))) < Small);
        }

        [Fact]
        public void A_replay_over_256_mb_is_skipped_and_logged()
        {
            string path = _game.Write(@"user\Replays\Huge.rep", ReplayHeaderTests.Replay("M=00maps/tournament desert;"));
            using (var file = new FileStream(path, FileMode.Open))
                file.SetLength(256L * 1024 * 1024 + 1);
            Assert.Null(ReplayService.Read(path));
            Assert.Null(ReplayService.ReadResults(path));
            Assert.Single(LogLines(), line => line.Contains(path));
        }

        [Fact]
        public void A_replay_header_text_with_no_end_is_refused_without_a_large_allocation()
        {
            // "GENREP", times, frames, flags and disconnects, then a title that never ends
            string path = _game.Write(@"user\Replays\Endless.rep", [.. "GENREP"u8, .. new byte[22], .. Enumerable.Repeat((byte)'A', 1_200_000)]);
            ReplayEntry? replay = null;
            Assert.True(Allocated(() => replay = ReplayService.Read(path)) < Small);
            Assert.Null(replay);
            Assert.Single(LogLines(), line => line.Contains(path));
        }

        private const string Picture = "  TextureWidth = 512\n  TextureHeight = 512\n  Coords = Left:1 Top:2 Right:3 Bottom:4\nEnd\n";

        [Fact]
        public void Texture_names_that_leave_the_texture_folders_and_coordinates_that_overflow_are_ignored()
        {
            _game.Write(@"game\Data\INI\MappedImages\HandCreated\Hostile.ini",
                "MappedImage Good\n  Texture = Interface512_001.tga\n" + Picture +
                "MappedImage Network\n  Texture = \\\\server\\share\\x.tga\n" + Picture +
                "MappedImage Rooted\n  Texture = C:\\Windows\\x.tga\n" + Picture +
                "MappedImage DriveRelative\n  Texture = C:x.tga\n" + Picture +
                "MappedImage Up\n  Texture = ..\\..\\x.tga\n" + Picture +
                "MappedImage UpForward\n  Texture = ../x.tga\n" + Picture +
                "MappedImage Huge\n  Texture = Interface512_002.tga\n  TextureWidth = 512\n  TextureHeight = 512\n" +
                "  Coords = Left:99999999999 Top:0 Right:1 Bottom:1\nEnd\n");

            // A picture where a name with ".." would lead from Art\Textures
            _game.Write(@"game\x.tga", Images.Tga(1, 1, 24, topDown: true, rle: false, 1, 2, 3));

            var images = new GameImages(new GameFiles(_game.Game, communityPatch: false), "English");
            Assert.NotNull(images.Find("Good"));
            foreach (string name in new[] { "Network", "Rooted", "DriveRelative", "Up", "UpForward", "Huge" })
                Assert.Null(images.Find(name));
            Assert.Null(images.ResolveTexture(@"..\..\x.tga"));
        }

        public void Dispose() => _game.Dispose();
    }
}
