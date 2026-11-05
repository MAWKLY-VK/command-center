using System.Text;

namespace CommandCenter.Tests
{
    public class OptionsFileTests
    {
        private static readonly Encoding Latin1 = Encoding.Latin1;

        [Fact]
        public void Only_the_changed_keys_are_rewritten()
        {
            // The player name holds a byte that is not ASCII; it has to come back exactly as it was
            byte[] current = Latin1.GetBytes("Resolution = 800 600\r\nUserName = André\r\nResolution = 1024 768\r\n");
            byte[] result = OptionsFile.With(current, new Dictionary<string, string> { ["Resolution"] = "1920 1080", ["Gamma"] = "50" });
            Assert.Equal(Latin1.GetBytes("Resolution = 1920 1080\r\nUserName = André\r\nResolution = 1920 1080\r\nGamma = 50\r\n"), result);
        }

        [Fact]
        public void Line_endings_stay_as_they_were()
        {
            byte[] result = OptionsFile.With(Latin1.GetBytes("A = 1\nB = 2\n"), new Dictionary<string, string> { ["B"] = "3" });
            Assert.Equal("A = 1\nB = 3\n", Latin1.GetString(result));
        }

        [Fact]
        public void Setting_the_same_value_changes_nothing()
        {
            byte[] current = Latin1.GetBytes("A = 1\r\nB = 2\r\n");
            Assert.Equal(current, OptionsFile.With(current, new Dictionary<string, string> { ["B"] = "2" }));
        }

        [Fact]
        public void The_last_of_two_lines_wins_like_in_the_game()
        {
            var values = OptionsFile.Parse(new[] { "Gamma = 40", "junk line", "gamma = 60", "= no key" });
            Assert.Equal("60", values["Gamma"]);
            Assert.Single(values);
        }
    }

    public class CsfFileTests
    {
        // A string table with one label, written the way the game stores it (text bytes inverted)
        private static byte[] Table(string label, string text)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write(Encoding.ASCII.GetBytes(" FSC"));
            w.Write(3);    // version
            w.Write(1);    // labels
            w.Write(1);    // strings
            w.Write(0);
            w.Write(0);    // language
            w.Write(Encoding.ASCII.GetBytes(" LBL"));
            w.Write(1);
            w.Write(label.Length);
            w.Write(Encoding.ASCII.GetBytes(label));
            w.Write(Encoding.ASCII.GetBytes(" RTS"));
            w.Write(text.Length);
            w.Write(Encoding.Unicode.GetBytes(text).Select(b => (byte)~b).ToArray());
            w.Flush();
            return stream.ToArray();
        }

        [Fact]
        public void A_table_is_read_and_written_back_unchanged()
        {
            byte[] data = Table("CONTROLBAR:ConstructAmericaVehicleHumvee", "&Humvee");
            var csf = CsfFile.Load(data);
            Assert.Equal("&Humvee", csf.Get("controlbar:constructamericavehiclehumvee"));
            Assert.Equal(data, csf.ToBytes());
        }

        [Fact]
        public void A_changed_label_is_saved()
        {
            var csf = CsfFile.Load(Table("CONTROLBAR:Ranger", "&Ranger"));
            csf.Set("CONTROLBAR:Ranger", "R&anger");
            Assert.Equal("R&anger", CsfFile.Load(csf.ToBytes()).Get("CONTROLBAR:Ranger"));
        }

        [Theory]
        [InlineData("Hum&vee", 'V')]
        [InlineData("&Ranger", 'R')]
        [InlineData("Rock && Roll", '\0')]
        [InlineData("No marker", '\0')]
        [InlineData(null, '\0')]
        public void The_hotkey_is_the_letter_after_the_marker(string? text, char key) => Assert.Equal(key, CsfFile.HotkeyOf(text));

        [Theory]
        [InlineData("Hum&vee", 'H', "&Humvee")]
        [InlineData("&Tank Hunter", 'A', "T&ank Hunter")]
        [InlineData("&Tank Hunter", 'U', "Tank H&unter")]
        [InlineData("Tank", 'X', "Tank (&X)")]
        [InlineData("Rock && &Roll", 'L', "Rock && Ro&ll")]
        public void A_new_hotkey_moves_the_marker(string original, char key, string expected) =>
            Assert.Equal(expected, CsfFile.WithHotkey(original, key));

        [Fact]
        public void Removing_the_hotkey_keeps_a_literal_ampersand() => Assert.Equal("Rock && Roll", CsfFile.WithoutHotkey("Rock && &Roll"));
    }

    public class SmallFormatTests
    {
        [Fact]
        public void Crc32_matches_the_standard_check_value()
        {
            using var file = new TempFile("123456789");
            Assert.Equal(0xCBF43926u, Crc32.OfFile(file.Path));
        }

        [Theory]
        [InlineData("EnglishZH.big", true)]
        [InlineData("GermanZH.big", true)]
        [InlineData("frenchzh.big", true)]
        [InlineData("INIZH.big", false)]
        [InlineData("AudioEnglishZH.big", false)]
        [InlineData("W3DEnglishZH.big", false)]
        [InlineData("EnglishZH.big.bak", false)]
        public void Every_edition_has_a_language_archive(string name, bool language) =>
            Assert.Equal(language, HealthService.IsLanguageArchive(name));

        [Fact]
        public void The_game_map_cache_gives_players_and_start_positions()
        {
            using var file = new TempFile(
                "MapCache user_5Cmaps_5Cmy_20map_5Cmy_20map_2Emap\r\n" +
                "  numPlayers = 2\r\n" +
                "  extentMax = X:1000.00 Y:800.00 Z:0.00\r\n" +
                "  Player_1_Start = X:100.00 Y:200.00 Z:0.00\r\n" +
                "  Player_2_Start = X:900.00 Y:600.00 Z:0.00\r\n" +
                "END\r\n");
            var cache = MapCatalog.ReadMapCache(file.Path);
            var entry = Assert.Contains("my map.map", (IDictionary<string, MapCatalog.GameCacheEntry>)cache);
            Assert.Equal(2, entry.Players);
            Assert.Equal(2, entry.Starts.Count);
            Assert.Equal(0.1, entry.Starts[0].X, 6);
            Assert.Equal(0.75, entry.Starts[0].Y, 6);
            Assert.Equal(0.9, entry.Starts[1].X, 6);
            Assert.Equal(0.25, entry.Starts[1].Y, 6);
        }
    }

    public class ReplayHeaderTests
    {
        private static byte[] Replay(string options, uint start = 1_760_000_000, uint end = 1_760_000_754)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            void Wide(string s) { w.Write(Encoding.Unicode.GetBytes(s)); w.Write((ushort)0); }
            w.Write(Encoding.ASCII.GetBytes("GENREP"));
            w.Write(start);
            w.Write(end);
            w.Write(18_000u);           // frames
            w.Write((byte)0);           // desync
            w.Write((byte)1);           // quit early
            w.Write(new byte[8]);       // disconnects
            Wide("Last Replay");
            w.Write(new byte[16]);      // SYSTEMTIME
            Wide("Version 1.04");
            Wide("");                   // build date
            w.Write(new byte[12]);      // version number and CRCs
            w.Write(Encoding.UTF8.GetBytes(options));
            w.Write((byte)0);
            w.Flush();
            return stream.ToArray();
        }

        [Fact]
        public void The_header_gives_map_players_and_length()
        {
            const string options = "US=1;M=00maps/tournament desert;MC=1A2B;MS=123;SD=42;C=100;SR=0;SC=10000;O=N;" +
                                   "S=HAlice,0,8088,TT,1,2,-1,0,1:HBob,0,8088,TT,2,3,-1,1,1:X:X:X:X:X:X;";
            using var file = new TempFile(Replay(options));
            var replay = ReplayService.Read(file.Path);

            Assert.NotNull(replay);
            Assert.Equal("tournament desert", replay!.Map);
            Assert.Equal("1.04", replay.Version);
            Assert.Equal(10000, replay.StartingCash);
            Assert.Equal(TimeSpan.FromSeconds(754), replay.Length);
            Assert.True(replay.QuitEarly);
            Assert.Equal(new[] { "Alice", "Bob" }, replay.Players.Select(p => p.Name));
            Assert.Equal(Faction.Usa, replay.Players[0].Faction);
            Assert.Equal(Faction.China, replay.Players[1].Faction);
            Assert.Equal("1v1", replay.Mode);
        }

        [Fact]
        public void A_computer_opponent_makes_it_a_skirmish()
        {
            using var file = new TempFile(Replay("M=00maps/alpine assault;S=HAlice,0,8088,TT,1,2,-1,0,1:CH,3,4,-1,1:X:X:X:X:X:X;"));
            var replay = ReplayService.Read(file.Path);
            Assert.Equal("Skirmish", replay!.Mode);
            Assert.Equal("Hard Army", replay.Players[1].Name);
            Assert.Equal(Faction.Gla, replay.Players[1].Faction);
        }

        [Fact]
        public void Another_file_is_not_a_replay()
        {
            using var file = new TempFile("not a replay at all");
            Assert.Null(ReplayService.Read(file.Path));
        }
    }
}
