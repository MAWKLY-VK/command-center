using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace CommandCenter.Services
{
    // Zero Hour draws every character on its own, left to right (render2dsentence.cpp: one ExtTextOutW per character, no
    // shaping and no right-to-left logic), so Arabic has to arrive joined and in display order:
    //  - each letter in its Presentation Forms-B shape: alone, at the end, at the start or in the middle of a word;
    //  - lam followed by alef as one ligature;
    //  - every line reversed, while numbers, Latin words, printf tokens such as %d and the hotkey marker keep their own
    //    left-to-right order, and brackets inside the Arabic are mirrored.
    // The same approach as the community patch and TheSuperHackers/GeneralsGameCode issue #2.
    public static class ArabicShaping
    {
        // Each letter's shapes: alone, at the end, at the start, in the middle ('\0' where it has none)
        private static readonly Dictionary<char, char[]> Forms = BuildForms();

        private static Dictionary<char, char[]> BuildForms()
        {
            // Letter, its first Presentation Forms-B code point, and how many shapes follow it there (1, 2 or 4)
            (int Letter, int First, int Count)[] table =
            {
                (0x0621, 0xFE80, 1), (0x0622, 0xFE81, 2), (0x0623, 0xFE83, 2), (0x0624, 0xFE85, 2), (0x0625, 0xFE87, 2),
                (0x0626, 0xFE89, 4), (0x0627, 0xFE8D, 2), (0x0628, 0xFE8F, 4), (0x0629, 0xFE93, 2), (0x062A, 0xFE95, 4),
                (0x062B, 0xFE99, 4), (0x062C, 0xFE9D, 4), (0x062D, 0xFEA1, 4), (0x062E, 0xFEA5, 4), (0x062F, 0xFEA9, 2),
                (0x0630, 0xFEAB, 2), (0x0631, 0xFEAD, 2), (0x0632, 0xFEAF, 2), (0x0633, 0xFEB1, 4), (0x0634, 0xFEB5, 4),
                (0x0635, 0xFEB9, 4), (0x0636, 0xFEBD, 4), (0x0637, 0xFEC1, 4), (0x0638, 0xFEC5, 4), (0x0639, 0xFEC9, 4),
                (0x063A, 0xFECD, 4), (0x0641, 0xFED1, 4), (0x0642, 0xFED5, 4), (0x0643, 0xFED9, 4), (0x0644, 0xFEDD, 4),
                (0x0645, 0xFEE1, 4), (0x0646, 0xFEE5, 4), (0x0647, 0xFEE9, 4), (0x0648, 0xFEED, 2), (0x0649, 0xFEEF, 2),
                (0x064A, 0xFEF1, 4),
            };
            var forms = new Dictionary<char, char[]>();
            foreach (var (letter, first, count) in table)
            {
                forms[(char)letter] = new[]
                {
                    (char)first,
                    count >= 2 ? (char)(first + 1) : '\0',
                    count == 4 ? (char)(first + 2) : '\0',
                    count == 4 ? (char)(first + 3) : '\0',
                };
            }
            return forms;
        }

        // Lam followed by one of the alefs: the ligature alone and at the end of a word
        private static readonly Dictionary<char, (char Alone, char End)> LamAlef = new()
        {
            ['آ'] = ('ﻵ', 'ﻶ'),
            ['أ'] = ('ﻷ', 'ﻸ'),
            ['إ'] = ('ﻹ', 'ﻺ'),
            ['ا'] = ('ﻻ', 'ﻼ'),
        };

        private const char Lam = 'ل';

        private static bool CanJoinBefore(char letter) => Forms.TryGetValue(letter, out var f) && f[2] != '\0';
        private static bool CanJoinAfter(char letter) => Forms.TryGetValue(letter, out var f) && f[1] != '\0';

        public static bool IsArabic(char c) => c is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

        public static bool HasArabic(string? text) => text != null && text.Any(IsArabic);

        // Text as a translator writes it, made safe for the game: Persian yeh and kaf become the Arabic letters (the
        // Persian yeh has no dots at the end of a word), Arabic-Indic digits become 0-9 (the numbers the game fills in are
        // 0-9 too), and harakat and tatweel go (a mark drawn as a character of its own would sit beside its letter)
        public static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                switch (c)
                {
                    case 'ی': sb.Append('ي'); break;
                    case 'ک': sb.Append('ك'); break;
                    case >= '٠' and <= '٩': sb.Append((char)('0' + (c - '٠'))); break;
                    case >= '۰' and <= '۹': sb.Append((char)('0' + (c - '۰'))); break;
                    case >= 'ً' and <= 'ْ':
                    case 'ٰ':
                    case 'ـ':
                        break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // One run of Arabic letters, still in reading order, with each letter in its joined shape
        public static string Shape(string word)
        {
            var sb = new StringBuilder(word.Length);
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                char prev = i > 0 ? word[i - 1] : '\0';
                bool joinsPrevious = CanJoinBefore(prev) && CanJoinAfter(c);
                if (c == Lam && i + 1 < word.Length && LamAlef.TryGetValue(word[i + 1], out var ligature))
                {
                    sb.Append(joinsPrevious ? ligature.End : ligature.Alone);
                    i++;
                    continue;
                }
                if (!Forms.TryGetValue(c, out var forms))
                {
                    sb.Append(c);
                    continue;
                }
                char next = i + 1 < word.Length ? word[i + 1] : '\0';
                bool joinsNext = CanJoinBefore(c) && CanJoinAfter(next);
                sb.Append(joinsPrevious && joinsNext ? forms[3] : joinsPrevious ? forms[1] : joinsNext ? forms[2] : forms[0]);
            }
            return sb.ToString();
        }

        // printf tokens the game fills in (%d, %ls, %02d, %.0f...: the conversions its strings use, so the "% e" in
        // "25% extra" is no token), "%%", and a hotkey marker with its key
        private const string Token = @"%(?:%|[-+#0]*\d*(?:\.\d+)?(?:hh|h|ll|l|I64)?[diucsSf])";

        private static readonly Regex Units = new(
            @"(?<ltr>" + Token +
            @"|&[A-Za-z0-9]" +
            // a Latin word or number, with a percent sign after it ("25%") unless that sign starts "%%" or a token
            @"|[A-Za-z0-9](?:[A-Za-z0-9&._:'/+\-]*[A-Za-z0-9])?(?:%(?![%\-+#0-9.hlIdiucsSf]))?)" +
            @"|(?<rtl>[؀-ۿﭐ-﷿ﹰ-﻿]+)" +
            @"|(?<n>.)",
            RegexOptions.Compiled | RegexOptions.Singleline);

        public static readonly Regex PrintfToken = new(Token, RegexOptions.Compiled);

        // The printf tokens in the order the game fills them in (left to right in the stored text); "%%" is not one
        public static List<string> Tokens(string text) =>
            PrintfToken.Matches(text).Select(m => m.Value).Where(t => t != "%%").ToList();

        // Tokens and "%%" together, in order: a "%%" moved in front of a token would turn that token into plain text
        public static List<string> PercentSequence(string text) =>
            PrintfToken.Matches(text).Select(m => m.Value).ToList();

        private static readonly Dictionary<char, char> Mirror = new()
        {
            ['('] = ')', [')'] = '(', ['['] = ']', [']'] = '[', ['{'] = '}', ['}'] = '{', ['<'] = '>', ['>'] = '<', ['«'] = '»', ['»'] = '«',
        };

        // One line of a right-to-left paragraph in the order the game draws it, left to right
        public static string ToVisualLine(string line)
        {
            var units = new List<(string Text, char Kind)>();
            foreach (Match m in Units.Matches(line))
            {
                if (m.Groups["ltr"].Success)
                    units.Add((m.Value, 'L'));
                else if (m.Groups["rtl"].Success)
                    units.Add((Shape(m.Value), 'R'));
                else
                    units.Add((m.Value, 'N'));
            }

            // Spaces and punctuation between two left-to-right parts stay with them; all others follow the Arabic
            var direction = new char[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Kind != 'N')
                {
                    direction[i] = units[i].Kind;
                    continue;
                }
                char before = 'R', after = 'R';
                for (int j = i - 1; j >= 0; j--)
                    if (units[j].Kind != 'N') { before = units[j].Kind; break; }
                for (int j = i + 1; j < units.Count; j++)
                    if (units[j].Kind != 'N') { after = units[j].Kind; break; }
                direction[i] = before == 'L' && after == 'L' ? 'L' : 'R';
            }

            // Runs of one direction, last run first; an Arabic run is reversed and its brackets mirrored
            var runs = new List<(StringBuilder Text, char Direction)>();
            for (int i = 0; i < units.Count; i++)
            {
                if (runs.Count == 0 || runs[^1].Direction != direction[i])
                    runs.Add((new StringBuilder(), direction[i]));
                runs[^1].Text.Append(units[i].Text);
            }
            var visual = new StringBuilder(line.Length);
            for (int r = runs.Count - 1; r >= 0; r--)
            {
                string text = runs[r].Text.ToString();
                if (runs[r].Direction == 'L')
                {
                    visual.Append(text);
                    continue;
                }
                for (int k = text.Length - 1; k >= 0; k--)
                    visual.Append(Mirror.TryGetValue(text[k], out char mirrored) ? mirrored : text[k]);
            }
            return visual.ToString().Trim();
        }

        // Text in reading order made ready for the game: lines kept, a line wider than maxWidth (in em of Arial; 0 means
        // no limit) broken at spaces first, since a line the game wraps itself would show its parts bottom to top
        public static string ToVisual(string text, double maxWidth = 0)
        {
            var lines = new List<string>();
            foreach (string line in Normalize(text).Replace("\r", "").Split('\n'))
            {
                string trimmed = line.Trim();
                if (maxWidth <= 0 || Width(ToVisualLine(trimmed)) <= maxWidth)
                {
                    lines.Add(trimmed);
                    continue;
                }
                var current = new StringBuilder();
                foreach (string word in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = current.Length == 0 ? word : current + " " + word;
                    if (current.Length > 0 && Width(ToVisualLine(candidate)) > maxWidth)
                    {
                        lines.Add(current.ToString());
                        current.Clear().Append(word);
                    }
                    else
                    {
                        current.Clear().Append(candidate);
                    }
                }
                if (current.Length > 0)
                    lines.Add(current.ToString());
            }
            return string.Join("\n", lines.Select(ToVisualLine));
        }

        // ── Width ──
        // Measured with Arial, the font the game draws Arabic with (see ArabicGameText). Latin text is drawn with the
        // window's font, which for most windows is the game's "Generals" face: Arial condensed to 40% of the height
        // (render2dsentence.cpp), about 9/10 of Arial's own width.
        private const double LatinScale = 0.9;

        private static readonly Lazy<GlyphTypeface?> Arial = new(() =>
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
                return File.Exists(path) ? new GlyphTypeface(new Uri(path)) : null;
            }
            catch
            {
                return null;
            }
        });

        // Width in em; a character the font lacks counts as half an em
        public static double Width(string text)
        {
            var font = Arial.Value;
            double width = 0;
            foreach (char c in text)
            {
                double advance = font != null && font.CharacterToGlyphMap.TryGetValue(c, out ushort glyph) ? font.AdvanceWidths[glyph] : 0.5;
                width += c < 0x100 ? advance * LatinScale : advance;
            }
            return width;
        }
    }
}
