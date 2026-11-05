using System.IO;
using System.Text;

namespace CommandCenter.Core.Content
{
    // Takes out the Arabic game text that Command Center 0.4.0 could turn on, which later versions no longer have
    // (ADR 0005). 0.4.0 wrote two files in a language folder of the game:
    //  - Data\<language>\generals.csf: the game's own text table with Arabic in it. The hotkey editor writes the same
    //    loose file, so it also holds the hotkeys the player saved, and other tools may have added labels to it. A
    //    marker label records that the Arabic is ours.
    //  - Data\<language>\Language\CommandCenterArabic.ini: makes the game draw Arabic letters with Arial.
    // Only 0.4.0 shipped the feature (its Arabic table was version 4), so the facts below are all that is needed.
    public static class ArabicGameTextRemoval
    {
        internal const string MarkerLabel = "CommandCenter:ArabicGameText";

        // The labels 0.4.0 added because Generals Online asks for them and the game's table lacks them
        internal static readonly IReadOnlySet<string> AddedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GUI:ACErrorHeader", "GUI:ACExternalProcessNotRunning", "GUI:ACLobbyIntegrityError",
            "GUI:JoinFailedAnticheatMismatch", "GUI:NoARMSupport", "GUI:NoARMSupportHeader",
            "GUI:PathfindingCrashPrevented", "GUI:ReplayLoadFailed", "GUI:ReplayLoadFailedTitle",
            "GUI:ReplayMapNotFound", "GUI:ReplayMapNotFoundTitle", "GUI:SelectNextIdleWorker",
            "GUI:SelectNextIdleWorkerDescription", "GUI:VeryHigh",
        };

        // The title of the changes in the fix history
        public const string Title = "Arabic game text removed";

        public static string TablePath(string language) => $@"Data\{language}\generals.csf";

        public static string FontFilePath(string language) => $@"Data\{language}\Language\CommandCenterArabic.ini";

        // Byte for byte the font file 0.4.0 wrote; a file with any other text is left alone
        private static readonly byte[] FontFile = Encoding.ASCII.GetBytes(
            "; Written by Command Center for the Arabic game text: letters at U+0100 and above are drawn with this font\r\n" +
            "Language\r\n" +
            "  UnicodeFontName = Arial\r\n" +
            "End\r\n");

        // A language folder that holds 0.4.0's Arabic game text: its table with the marker, its font file, or both
        public sealed record Leftover(string Language, bool Table, bool FontFile);

        public static List<Leftover> Find(string gameFolder)
        {
            var found = new List<Leftover>();
            string data = Path.Combine(gameFolder, "Data");
            if (!Directory.Exists(data))
                return found;
            foreach (string folder in Directory.EnumerateDirectories(data))
            {
                string language = Path.GetFileName(folder);
                bool table = HasMarker(Path.Combine(gameFolder, TablePath(language)));
                bool font = IsOurFontFile(Path.Combine(gameFolder, FontFilePath(language)));
                if (table || font)
                    found.Add(new Leftover(language, table, font));
            }
            return found;
        }

        // Puts the English back and moves the font file to quarantine in every language folder, each with a backup;
        // returns the changes, for Undo. A folder whose own table is not in the game's archives keeps its table.
        // When a change fails, the changes made before it are undone where possible, and the error is thrown.
        public static List<BackupEntry> Remove(string gameFolder)
        {
            var leftovers = Find(gameFolder);
            var changes = new List<BackupEntry>();
            if (leftovers.Count == 0)
                return changes;

            try
            {
                var files = new GameFiles(gameFolder);
                foreach (var leftover in leftovers)
                {
                    string table = TablePath(leftover.Language);
                    if (leftover.Table && files.ReadFromArchive(table) is { } gameTable)
                    {
                        string target = files.LoosePath(table);
                        byte[] english = EnglishTable(gameTable, File.ReadAllBytes(target));
                        // Nothing else left: the game reads its own table again, and a later game update comes through
                        changes.Add(english.AsSpan().SequenceEqual(gameTable)
                            ? BackupService.MoveToQuarantine(Title, target, $"{table} · the game's own table is used again")
                            : BackupService.WriteFile(Title, target, english, $"{table} · English again, hotkeys kept"));
                    }
                    if (leftover.FontFile)
                    {
                        string font = FontFilePath(leftover.Language);
                        changes.Add(BackupService.MoveToQuarantine(Title, files.LoosePath(font), font));
                    }
                }
                return changes;
            }
            catch
            {
                for (int i = changes.Count - 1; i >= 0; i--)
                {
                    // The operation log already has the reason when an undo fails; the first error is the one to report
                    try { BackupService.Undo(changes[i]); }
                    catch (Exception) { }
                }
                throw;
            }
        }

        // Undoes all the changes, or none of them: false, with the reason, while the game runs or when a file changed
        // since (for example, hotkeys were saved after the removal)
        public static bool TryUndo(IReadOnlyList<BackupEntry> changes, out string reason)
        {
            if (GameLauncher.IsGameRunning())
            {
                reason = BackupService.GameRunningMessage;
                return false;
            }
            if (changes.FirstOrDefault(c => !BackupService.CanUndo(c)) is { } changed)
            {
                reason = $"{Path.GetFileName(changed.Target)} changed after the Arabic game text was removed, so nothing was undone.";
                return false;
            }
            for (int i = changes.Count - 1; i >= 0; i--)
                BackupService.Undo(changes[i]);
            reason = "";
            return true;
        }

        private static bool HasMarker(string table)
        {
            try
            {
                return File.Exists(table) && CsfFile.Load(File.ReadAllBytes(table)).Contains(MarkerLabel);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A table that cannot be read is not one 0.4.0 wrote
                return false;
            }
        }

        private static bool IsOurFontFile(string font)
        {
            try
            {
                return File.Exists(font) && File.ReadAllBytes(font).AsSpan().SequenceEqual(FontFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The game's own table again, keeping from the table in use the hotkeys the player saved and the labels that
        // other tools added. It starts from the game's own table, so text that a game update changed comes through.
        // This is what 0.4.0's own "turn off" gave.
        internal static byte[] EnglishTable(byte[] gameTable, byte[] tableInUse)
        {
            var game = CsfFile.Load(gameTable);
            var inUse = CsfFile.Load(tableInUse);
            var english = CsfFile.Load(gameTable);
            foreach (string label in game.Labels)
            {
                if (inUse.Get(label) is not { } text)
                    continue;
                string original = game.Get(label) ?? "";
                char key = CsfFile.HotkeyOf(text);
                if (key != CsfFile.HotkeyOf(original))
                    english.Set(label, CsfFile.WithKey(original, key));
            }
            foreach (string label in inUse.Labels.Where(l => !game.Contains(l) && l != MarkerLabel && !AddedLabels.Contains(l)).ToList())
                english.Add(label, inUse.Get(label) ?? "");
            return english.ToBytes();
        }
    }
}
