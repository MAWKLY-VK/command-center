namespace CommandCenter.Tests
{
    // How the game text table that 0.4.0's Arabic game text wrote becomes English again: the game's own table comes
    // back, with the hotkeys the player saved and the labels that other tools added. The tables are made by hand,
    // because game files must not enter the repository. The expected results were pinned on 0.4.0's own "turn off"
    // before that code was removed, so the removal gives players exactly what 0.4.0 would have given them.
    public class EnglishRestoreTests
    {
        private const string Humvee = "CONTROLBAR:ConstructAmericaVehicleHumvee";

        private static readonly byte[] GameTable = CsfTables.Of(
            (Humvee, "Hum&vee"),
            ("OBJECT:Humvee", "Humvee"),
            ("GUI:Ok", "OK"));

        // Arabic letters in the forms 0.4.0 stored them (joined, each line reversed), as escapes
        private const string Arabic = "ﻲﻔﻤﻫ";

        // The labels 0.4.0 added because Generals Online asks for them and the game's table lacks them (the "added"
        // list of its Arabic table, translation version 4, the only version it shipped)
        public static TheoryData<string> AddedLabels => new()
        {
            "GUI:ACErrorHeader", "GUI:ACExternalProcessNotRunning", "GUI:ACLobbyIntegrityError",
            "GUI:JoinFailedAnticheatMismatch", "GUI:NoARMSupport", "GUI:NoARMSupportHeader",
            "GUI:PathfindingCrashPrevented", "GUI:ReplayLoadFailed", "GUI:ReplayLoadFailedTitle",
            "GUI:ReplayMapNotFound", "GUI:ReplayMapNotFoundTitle", "GUI:SelectNextIdleWorker",
            "GUI:SelectNextIdleWorkerDescription", "GUI:VeryHigh",
        };

        // The table as 0.4.0 left it: every label in Arabic, the button's hotkey in brackets after its Arabic name,
        // then any further labels, then the marker with its stamp (translation version; start of the table's hash)
        private static byte[] TableInUse(char humveeKey = 'V', params (string Label, string Text)[] more) =>
            CsfTables.Of(new[]
                {
                    (Humvee, $"{Arabic} (&{humveeKey})"),
                    ("OBJECT:Humvee", Arabic),
                    ("GUI:Ok", Arabic),
                }
                .Concat(more)
                .Append(("CommandCenter:ArabicGameText", "4;0123456789ABCDEF"))
                .ToArray());

        private static byte[] Restore(byte[] gameTable, byte[] tableInUse) =>
            ArabicGameText.Strip(gameTable, CsfFile.Load(tableInUse)).ToBytes();

        [Fact]
        public void With_nothing_else_left_the_games_own_table_comes_back_byte_for_byte() =>
            Assert.Equal(GameTable, Restore(GameTable, TableInUse()));

        [Fact]
        public void A_saved_hotkey_is_kept() =>
            Assert.Equal(
                CsfTables.Of((Humvee, "&Humvee"), ("OBJECT:Humvee", "Humvee"), ("GUI:Ok", "OK")),
                Restore(GameTable, TableInUse(humveeKey: 'H')));

        [Fact]
        public void Labels_that_other_tools_added_are_kept() =>
            Assert.Equal(
                CsfTables.Of((Humvee, "Hum&vee"), ("OBJECT:Humvee", "Humvee"), ("GUI:Ok", "OK"), ("OTHERTOOL:Note", "From another tool")),
                Restore(GameTable, TableInUse(more: ("OTHERTOOL:Note", "From another tool"))));

        [Theory]
        [MemberData(nameof(AddedLabels))]
        public void The_marker_and_each_label_0_4_0_added_are_dropped(string added) =>
            Assert.Equal(GameTable, Restore(GameTable, TableInUse(more: (added, Arabic))));
    }
}
