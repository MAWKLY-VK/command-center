namespace CommandCenter.Tests
{
    // Finding and removing 0.4.0's Arabic game text, in a game folder and a data folder made for each test. The game's
    // archives hold small hand-made tables: game files must not enter the repository.
    public class ArabicGameTextRemovalTests
    {
        private const string Humvee = "CONTROLBAR:ConstructAmericaVehicleHumvee";

        // Arabic letters in the forms 0.4.0 stored them, as escapes
        private const string Arabic = "ﻲﻔﻤﻫ";

        // Byte for byte the font file 0.4.0 wrote
        private const string FontFile =
            "; Written by Command Center for the Arabic game text: letters at U+0100 and above are drawn with this font\r\n" +
            "Language\r\n" +
            "  UnicodeFontName = Arial\r\n" +
            "End\r\n";

        private static readonly byte[] EnglishGameTable = CsfTables.Of((Humvee, "Hum&vee"), ("GUI:Ok", "OK"));
        private static readonly byte[] GermanGameTable = CsfTables.Of((Humvee, "Hum&vee"), ("GUI:Ok", "Ja"));

        private static byte[] ArabicTable(char humveeKey = 'V') => CsfTables.Of(
            (Humvee, $"{Arabic} (&{humveeKey})"),
            ("GUI:Ok", Arabic),
            ("GUI:VeryHigh", Arabic),
            ("CommandCenter:ArabicGameText", "4;0123456789ABCDEF"));

        // A game whose archives hold its English and German tables
        private static TempGame NewGame()
        {
            var game = new TempGame();
            BigArchives.Write(Path.Combine(game.Game, "EnglishZH.big"), (@"Data\English\generals.csf", EnglishGameTable));
            BigArchives.Write(Path.Combine(game.Game, "GermanZH.big"), (@"Data\German\generals.csf", GermanGameTable));
            return game;
        }

        // What 0.4.0's "turn on" left in a language folder, with the Humvee hotkey the player saved
        private static void TurnedOn(TempGame game, string language, char humveeKey = 'V')
        {
            game.Write($@"game\Data\{language}\generals.csf", ArabicTable(humveeKey));
            game.Write($@"game\Data\{language}\Language\CommandCenterArabic.ini", FontFile);
        }

        private static string Table(TempGame game, string language) => Path.Combine(game.Game, "Data", language, "generals.csf");

        private static string Font(TempGame game, string language) =>
            Path.Combine(game.Game, "Data", language, "Language", "CommandCenterArabic.ini");

        [Fact]
        public void Finds_the_table_and_the_font_file_in_every_language_folder()
        {
            using var game = NewGame();
            TurnedOn(game, "English");
            TurnedOn(game, "German");

            Assert.Equal(
                new[]
                {
                    new ArabicGameTextRemoval.Leftover("English", Table: true, FontFile: true),
                    new ArabicGameTextRemoval.Leftover("German", Table: true, FontFile: true),
                },
                ArabicGameTextRemoval.Find(game.Game).OrderBy(l => l.Language));
        }

        [Fact]
        public void The_english_comes_back_with_the_saved_hotkey_and_the_font_file_goes_to_quarantine()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');

            var changes = ArabicGameTextRemoval.Remove(game.Game);

            Assert.Equal(CsfTables.Of((Humvee, "&Humvee"), ("GUI:Ok", "OK")), File.ReadAllBytes(Table(game, "English")));
            Assert.False(File.Exists(Font(game, "English")));
            Assert.Equal(FontFile, File.ReadAllText(Assert.Single(changes, c => c.Kind == "move").BackupFile!));
            Assert.Empty(ArabicGameTextRemoval.Find(game.Game));
            Assert.Equal(changes.Select(c => c.Id).Order(), BackupService.Load().Select(e => e.Id).Order());
        }

        [Fact]
        public void Each_change_is_in_the_operation_log()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');

            ArabicGameTextRemoval.Remove(game.Game);

            var lines = File.ReadAllLines(OperationLog.FilePath)
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .Select(l => (l.GetProperty("operation").GetString(), l.GetProperty("target").GetString(), l.GetProperty("result").GetString()))
                .ToList();
            Assert.Contains(("write", Table(game, "English"), "done"), lines);
            Assert.Contains(("quarantine", Font(game, "English"), "done"), lines);
        }

        [Fact]
        public void What_0_4_0_did_not_write_is_left_alone()
        {
            using var game = NewGame();
            // The player's own font file, a table with only saved hotkeys, and our table where the game has no table
            byte[] font = System.Text.Encoding.ASCII.GetBytes(FontFile + "; changed by the player\r\n");
            byte[] hotkeysOnly = CsfTables.Of((Humvee, "&Humvee"), ("GUI:Ok", "OK"));
            game.Write(@"game\Data\English\Language\CommandCenterArabic.ini", font);
            game.Write(@"game\Data\English\generals.csf", hotkeysOnly);
            game.Write(@"game\Data\French\generals.csf", ArabicTable());

            var changes = ArabicGameTextRemoval.Remove(game.Game);

            Assert.Empty(changes);
            Assert.Equal(font, File.ReadAllBytes(Font(game, "English")));
            Assert.Equal(hotkeysOnly, File.ReadAllBytes(Table(game, "English")));
            Assert.Equal(ArabicTable(), File.ReadAllBytes(Table(game, "French")));
            Assert.Empty(BackupService.Load());
        }

        [Fact]
        public void Nothing_is_touched_while_the_game_runs()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');
            using var running = new GameRunning();

            Assert.Throws<InvalidOperationException>(() => ArabicGameTextRemoval.Remove(game.Game));

            Assert.Equal(ArabicTable('H'), File.ReadAllBytes(Table(game, "English")));
            Assert.Equal(FontFile, File.ReadAllText(Font(game, "English")));
            Assert.Empty(BackupService.Load());
        }

        [Fact]
        public void A_failure_in_the_middle_undoes_the_changes_made_before_it()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');

            // The table is changed first. Another program reads the font file, so it can be read but not moved.
            using (new FileStream(Font(game, "English"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => ArabicGameTextRemoval.Remove(game.Game));

            Assert.Equal(ArabicTable('H'), File.ReadAllBytes(Table(game, "English")));
            Assert.Equal(FontFile, File.ReadAllText(Font(game, "English")));
            Assert.Empty(BackupService.Load());
        }

        [Fact]
        public void Undo_puts_every_file_back()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');
            TurnedOn(game, "German");
            var changes = ArabicGameTextRemoval.Remove(game.Game);

            Assert.True(ArabicGameTextRemoval.TryUndo(changes, out string reason), reason);

            Assert.Equal(ArabicTable('H'), File.ReadAllBytes(Table(game, "English")));
            Assert.Equal(ArabicTable(), File.ReadAllBytes(Table(game, "German")));
            Assert.Equal(FontFile, File.ReadAllText(Font(game, "English")));
            Assert.Equal(FontFile, File.ReadAllText(Font(game, "German")));
            Assert.Empty(BackupService.Load());
        }

        [Fact]
        public void Undo_changes_nothing_when_a_file_changed_since()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');
            TurnedOn(game, "German");
            var changes = ArabicGameTextRemoval.Remove(game.Game);
            // Hotkeys saved after the removal
            byte[] saved = CsfTables.Of((Humvee, "Hu&mvee"), ("GUI:Ok", "OK"));
            File.WriteAllBytes(Table(game, "English"), saved);

            Assert.False(ArabicGameTextRemoval.TryUndo(changes, out string reason));

            Assert.Contains("generals.csf", reason);
            Assert.Equal(saved, File.ReadAllBytes(Table(game, "English")));
            Assert.False(File.Exists(Table(game, "German")));
            Assert.False(File.Exists(Font(game, "English")));
            Assert.False(File.Exists(Font(game, "German")));
            Assert.Equal(changes.Count, BackupService.Load().Count);
        }

        [Fact]
        public void Undo_waits_for_the_game_to_close()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');
            var changes = ArabicGameTextRemoval.Remove(game.Game);
            using var running = new GameRunning();

            Assert.False(ArabicGameTextRemoval.TryUndo(changes, out string reason));

            Assert.Equal(BackupService.GameRunningMessage, reason);
            Assert.False(File.Exists(Font(game, "English")));
            Assert.Equal(changes.Count, BackupService.Load().Count);
        }

        [Fact]
        public void A_table_with_nothing_else_left_goes_to_quarantine_so_the_game_reads_its_own()
        {
            using var game = NewGame();
            TurnedOn(game, "German");

            var changes = ArabicGameTextRemoval.Remove(game.Game);

            Assert.False(File.Exists(Table(game, "German")));
            var table = Assert.Single(changes, c => c.Target == Table(game, "German"));
            Assert.Equal("move", table.Kind);
            Assert.Equal(ArabicTable(), File.ReadAllBytes(table.BackupFile!));
        }
    }
}
