using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommandCenter.Services
{
    // Optional: shows the game's most used text in Arabic (the labels in Data/game-text-ar.json), and puts it back.
    //
    // How the game finds the text:
    //  - It reads Data\<language>\generals.csf, and a loose file there wins over the archives (FileSystem::openFile).
    //    Neither that file nor Data\<language>\Language\*.ini is part of the multiplayer check (GameEngine.cpp loads both
    //    without a CRC), so the translation never causes a mismatch.
    //  - The hotkey editor writes the same loose file. Both start from the game's own table: the keys saved in the file
    //    stay, and an Arabic button name gets its key after it as " (&X)" (HotKey.cpp takes the character after the
    //    first '&', which has to be a Latin letter to be pressed).
    //  - Letters at U+0100 and above are drawn with Language.ini's UnicodeFontName, "Arial Unicode MS", which Windows
    //    does not ship (render2dsentence.cpp, W3DGameFont.cpp). Language\CommandCenterArabic.ini, read after Language.ini
    //    (INI::loadFileDirectory), names Arial instead: it has every Arabic letter shape and the height of the game's font.
    // A label in the table marks the file as ours and records which game table and translation it was built from.
    public static class ArabicGameText
    {
        public const string Title = "Arabic game text";

        public const string MarkerLabel = "CommandCenter:ArabicGameText";

        internal const string FontIniText =
            "; Written by Command Center for the Arabic game text: letters at U+0100 and above are drawn with this font\r\n" +
            "Language\r\n" +
            "  UnicodeFontName = Arial\r\n" +
            "End\r\n";

        public enum State { Off, On, Outdated, GameHasArabic, NoGame }

        public static string CsfPath(string language) => $@"Data\{language}\generals.csf";

        public static string FontIniPath(string language) => $@"Data\{language}\Language\CommandCenterArabic.ini";

        // ── The translation ──

        public sealed class Table
        {
            public int Version { get; init; }
            public Dictionary<string, string> Text { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            // Labels Generals Online asks for but the game's table does not have (it shows its own English then)
            public HashSet<string> Added { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Lazy<Table> _translation = new(() =>
        {
            using var stream = typeof(ArabicGameText).Assembly.GetManifestResourceStream("GameTextAr.json")
                               ?? throw new InvalidOperationException("GameTextAr.json is not embedded");
            return Parse(stream);
        });

        public static Table Translation => _translation.Value;

        internal static Table Parse(Stream json)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var table = new Table { Version = root.GetProperty("version").GetInt32() };
            foreach (var group in root.GetProperty("text").EnumerateObject())
                foreach (var entry in group.Value.EnumerateObject())
                    table.Text[entry.Name] = entry.Value.GetString() ?? "";
            if (root.TryGetProperty("added", out var added))
                foreach (var label in added.EnumerateArray())
                    table.Added.Add(label.GetString() ?? "");
            return table;
        }

        // ── One label ──

        // The Arabic text the game should get for a label, with its button key; null when the label stays English
        // (no translation, or one whose values would be filled in the wrong places)
        public static string? LabelFor(string label, string english, char key) =>
            Translation.Text.TryGetValue(label, out var arabic) ? Visual(english, arabic, key) : null;

        // The values the game fills in (%d, %ls...) are written {1}, {2}... in the translation, numbered in the order of
        // the English. The game fills them in from left to right in the stored text, and a line is stored reversed, so
        // in an Arabic line a later value has to come first ("{2} ... {1}"); a translation that would put them out of
        // order stays English rather than show a player's name where the map's should be.
        private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{(\d+)\}");
        private static readonly System.Text.RegularExpressions.Regex Marker = new(@"QQ(\d+)QQ");

        internal static string? Visual(string english, string arabic, char key)
        {
            if (arabic.Trim().Length == 0 || ArabicShaping.Tokens(arabic).Count > 0)
                return null;
            var tokens = ArabicShaping.Tokens(english);
            var numbers = Placeholder.Matches(arabic).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            if (!numbers.OrderBy(n => n).SequenceEqual(Enumerable.Range(1, tokens.Count)))
                return null;

            string logical = Placeholder.Replace(arabic, m => $"QQ{m.Groups[1].Value}QQ");
            if (key != '\0')
                logical = logical.TrimEnd() + " (&" + char.ToUpperInvariant(key) + ")";
            string visual = ArabicShaping.ToVisual(logical, WrapWidth(english));
            var order = Marker.Matches(visual).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            if (!order.SequenceEqual(Enumerable.Range(1, tokens.Count)))
                return null;
            return Marker.Replace(visual, m => tokens[int.Parse(m.Groups[1].Value) - 1]);
        }

        // How wide a line may be: as wide as the widest line of the English text when it has several, the width of a
        // tooltip line when the English one is long enough that the game wraps it, else no limit (names stay on one line)
        internal static double WrapWidth(string english)
        {
            if (english.Contains('\n'))
                return english.Split('\n').Max(l => ArabicShaping.Width(l.Trim()));
            return english.Length > 60 ? ArabicShaping.Width(TooltipLine) : 0;
        }

        // A typical line of the game's own unit descriptions
        private const string TooltipLine = "Strong vs. vehicles, buildings, aircraft and infantry";

        // ── The whole table ──

        public static bool IsIn(CsfFile csf) => csf.Contains(MarkerLabel);

        // "<translation version>;<first 16 hex digits of the SHA-256 of the game's own table>"
        internal static string Stamp(byte[] original) =>
            $"{Translation.Version};{Convert.ToHexString(SHA256.HashData(original))[..16]}";

        // The game's table with the Arabic text in it, keeping the hotkeys saved in the table in use now
        internal static CsfFile Build(byte[] original, CsfFile current, out int translated) => Merge(original, current, arabic: true, out translated);

        // The game's table without our Arabic, keeping the hotkeys saved in the table in use now
        internal static CsfFile Strip(byte[] original, CsfFile current) => Merge(original, current, arabic: false, out _);

        // Always starts from the game's own table, so text an update added or changed comes through (a copy of an older
        // table would show "MISSING" for new labels). From the table in use it keeps the hotkeys (the only thing the
        // hotkey editor changes) and labels other tools added.
        private static CsfFile Merge(byte[] original, CsfFile current, bool arabic, out int translated)
        {
            var game = CsfFile.Load(original);
            var result = CsfFile.Load(original);
            foreach (string label in game.Labels)
            {
                if (current.Get(label) is not { } text)
                    continue;
                string english = game.Get(label) ?? "";
                char key = CsfFile.HotkeyOf(text);
                if (key != CsfFile.HotkeyOf(english))
                    result.Set(label, CsfFile.WithKey(english, key));
            }
            foreach (string label in current.Labels.Where(l => !game.Contains(l) && l != MarkerLabel && !Translation.Text.ContainsKey(l)).ToList())
                result.Add(label, current.Get(label) ?? "");

            translated = 0;
            if (!arabic)
                return result;
            foreach (var (label, translation) in Translation.Text)
            {
                string? english = game.Get(label);
                if (english == null && !Translation.Added.Contains(label))
                    continue;
                char key = english == null ? '\0' : CsfFile.HotkeyOf(result.Get(label));
                if (Visual(english ?? "", translation, key) is not { } text)
                    continue;
                result.Add(label, text);
                translated++;
            }
            result.Add(MarkerLabel, Stamp(original));
            return result;
        }

        public static State Check(GameFiles files, string language)
        {
            if (files.ReadFromArchive(CsfPath(language)) is not { } original)
                return State.NoGame;
            var current = CsfFile.Load(files.Read(CsfPath(language))!);
            if (!IsIn(current))
                return GameHasArabic(CsfFile.Load(original)) ? State.GameHasArabic : State.Off;
            bool fontInPlace = File.Exists(files.LoosePath(FontIniPath(language)));
            return current.Get(MarkerLabel) == Stamp(original) && fontInPlace ? State.On : State.Outdated;
        }

        // A game that already shows Arabic (a community patch, or a future Generals Online) needs nothing from us
        internal static bool GameHasArabic(CsfFile game) => game.Labels.Count(l => ArabicShaping.HasArabic(game.Get(l))) >= 20;

        // Writes the Arabic table and the font file; returns how many texts are in Arabic
        public static int Apply(GameFiles files, string language)
        {
            string path = CsfPath(language);
            byte[] original = files.ReadFromArchive(path) ?? throw new FileNotFoundException(path);
            var current = CsfFile.Load(files.Read(path)!);
            byte[] built = Build(original, current, out int translated).ToBytes();
            if (translated == 0)
                throw new InvalidOperationException(Loc.T("None of the game's texts could be put in Arabic, so nothing was changed."));

            string target = files.LoosePath(path);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(built))
                BackupService.WriteFile(Title, target, built, $"{path} · {translated} texts");

            string font = files.LoosePath(FontIniPath(language));
            byte[] fontBytes = Encoding.ASCII.GetBytes(FontIniText);
            if (!File.Exists(font) || !File.ReadAllBytes(font).AsSpan().SequenceEqual(fontBytes))
                BackupService.WriteFile(Title, font, fontBytes, FontIniPath(language));
            return translated;
        }

        // Puts the English back, keeping any hotkeys saved since; the font file goes when it is still ours
        public static void Remove(GameFiles files, string language)
        {
            string path = CsfPath(language);
            string target = files.LoosePath(path);
            if (File.Exists(target) && files.ReadFromArchive(path) is { } original)
            {
                var current = CsfFile.Load(File.ReadAllBytes(target));
                if (IsIn(current))
                {
                    byte[] english = Strip(original, current).ToBytes();
                    // Nothing else left in the file: the game's own table is used again
                    if (english.AsSpan().SequenceEqual(original) && BackupService.HasChanges(target))
                        BackupService.RestoreOriginal(target);
                    else
                        BackupService.WriteFile(Title, target, english, $"{path} · English again");
                }
            }

            string font = files.LoosePath(FontIniPath(language));
            if (File.Exists(font) && File.ReadAllText(font) == FontIniText && BackupService.HasChanges(font))
                BackupService.RestoreOriginal(font);
        }
    }
}
