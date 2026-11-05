namespace CommandCenter.Tests
{
    // Finding and removing 0.4.0's Arabic game text, in a game folder and a data folder made for each test. The game's
    // archives hold small hand-made tables: game files must not enter the repository.
    public class ArabicGameTextRemovalTests
    {
        private const string Humvee = "CONTROLBAR:ConstructAmericaVehicleHumvee";

        // Arabic letters in the forms 0.4.0 stored them, as escapes
        private const string Arabic = "\uFEF2\uFED4\uFEE4\uFEEB";

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

        // ── The one-time step at the first start ──

        private static AppSettings Settings(string? language = "en") => new() { Language = language };

        private static System.Text.Json.JsonElement SavedSettings() =>
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(GamePaths.AppData, "settings.json"))).RootElement;

        [Fact]
        public void The_step_runs_once_and_clears_the_old_language()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');
            var settings = Settings("ar");

            var result = ArabicGameTextRemoval.RunOnce(settings, windowsArabic: false);

            Assert.Equal(2, result?.Changes.Count);
            Assert.Empty(ArabicGameTextRemoval.Find(game.Game));
            Assert.True(SavedSettings().GetProperty("EnglishOnlyStepDone").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, SavedSettings().GetProperty("Language").ValueKind);
            // Arabic game text that turns up later is left to Health
            TurnedOn(game, "German");
            Assert.Null(ArabicGameTextRemoval.RunOnce(settings, windowsArabic: false));
            Assert.Equal(ArabicTable(), File.ReadAllBytes(Table(game, "German")));
        }

        [Fact]
        public void The_step_waits_while_the_game_runs_and_runs_at_the_next_start()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');
            var settings = Settings();

            using (new GameRunning())
                Assert.Null(ArabicGameTextRemoval.RunOnce(settings, windowsArabic: false));

            Assert.Equal(ArabicTable('H'), File.ReadAllBytes(Table(game, "English")));
            Assert.False(settings.EnglishOnlyStepDone);
            Assert.NotNull(ArabicGameTextRemoval.RunOnce(settings, windowsArabic: false));
            Assert.Empty(ArabicGameTextRemoval.Find(game.Game));
        }

        [Fact]
        public void A_step_that_fails_is_not_recorded()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');
            var settings = Settings();

            using (new FileStream(Font(game, "English"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => ArabicGameTextRemoval.RunOnce(settings, windowsArabic: false));

            Assert.False(settings.EnglishOnlyStepDone);
            Assert.False(File.Exists(Path.Combine(GamePaths.AppData, "settings.json")));
            Assert.Equal(ArabicTable('H'), File.ReadAllBytes(Table(game, "English")));
        }

        [Fact]
        public void After_undo_the_step_is_not_redone()
        {
            using var game = NewGame();
            TurnedOn(game, "English", humveeKey: 'H');
            var settings = Settings();
            var result = ArabicGameTextRemoval.RunOnce(settings, windowsArabic: false)!;
            Assert.True(ArabicGameTextRemoval.TryUndo(result.Changes, out string reason), reason);

            Assert.Null(ArabicGameTextRemoval.RunOnce(settings, windowsArabic: false));

            Assert.Equal(ArabicTable('H'), File.ReadAllBytes(Table(game, "English")));
        }

        [Theory]
        // The saved language, Windows' display language in Arabic, 0.4.0's Arabic game text in the game -> notice
        [InlineData("en", false, false, false)]
        [InlineData("en", true, false, false)]
        [InlineData("ar", false, false, true)]
        [InlineData(null, true, false, true)]
        [InlineData(null, false, false, false)]
        [InlineData("fr", true, false, true)]
        [InlineData("en", false, true, true)]
        public void The_notice_is_due_after_an_arabic_interface_or_changed_files(string? language, bool windowsArabic, bool arabicGameText, bool due)
        {
            using var game = NewGame();
            if (arabicGameText)
                TurnedOn(game, "English");

            Assert.Equal(due, ArabicGameTextRemoval.RunOnce(Settings(language), windowsArabic)?.NoticeDue);
        }

        [Fact]
        public void Without_a_game_folder_the_step_still_completes()
        {
            using var game = NewGame();
            GamePaths.Game = "";

            var result = ArabicGameTextRemoval.RunOnce(Settings("ar"), windowsArabic: false);

            Assert.Empty(result!.Changes);
            Assert.True(result.NoticeDue);
        }

        // ── Health ──

        [Fact]
        public void Health_warns_while_arabic_game_text_is_left_and_its_fix_removes_it()
        {
            using var game = NewGame();
            Assert.Null(HealthService.CheckArabicGameText());
            TurnedOn(game, "German");

            var warning = HealthService.CheckArabicGameText();

            Assert.Equal(HealthStatus.Warning, warning?.Status);
            warning!.Fix!();
            Assert.Empty(ArabicGameTextRemoval.Find(game.Game));
            Assert.Null(HealthService.CheckArabicGameText());
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
