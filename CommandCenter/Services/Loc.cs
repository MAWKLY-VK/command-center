using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace CommandCenter.Services
{
    // Translations looked up by their English text, gettext style, so wrapping a string costs nothing:
    //   C#    Loc.T("Play online")              Loc.T("Installed {0} maps", count)
    //         Loc.N(count, "1 map", "{0} maps") picks the plural form the language needs
    //   XAML  Text="{l:T Play online}"          with xmlns:l="clr-namespace:CommandCenter.Services"
    //
    // Each language is one JSON file in Localization/ (English text: translation), embedded in the program.
    // A missing or empty entry shows the English text. The table is built once at startup and only read
    // afterwards, so lookups are safe from background threads.
    public static class Loc
    {
        public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("ar", "العربية") };

        public static string Language { get; private set; } = "en";
        public static bool IsRightToLeft => Language == "ar";
        public static FlowDirection FlowDirection => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        // Formats numbers and dates in the text. Arabic keeps Western digits and the Gregorian calendar.
        public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

        // English unless Windows itself is in Arabic
        public static string SystemLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";

        // Separates the plural forms of one entry inside the table
        private const char FormMark = '\u0001';

        private static FrozenDictionary<string, string> _table = FrozenDictionary<string, string>.Empty;
        private static readonly ConcurrentDictionary<string, byte> _missing = new(StringComparer.Ordinal);

        // English strings that were asked for but have no translation yet (written next to --capture screenshots)
        public static IEnumerable<string> Missing => _missing.Keys.OrderBy(k => k, StringComparer.Ordinal);

        public static void Init(string? language)
        {
            string code = Normalize(language) ?? SystemLanguage;
            _table = code == "en" ? FrozenDictionary<string, string>.Empty : Load(code);
            Culture = code == "ar" ? ArabicCulture() : CultureInfo.CurrentCulture;
            Language = code;
        }

        public static string? Normalize(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return null;
            string code = language.Trim().ToLowerInvariant();
            code = code.Length > 2 ? code[..2] : code;
            return Languages.Any(l => l.Code == code) ? code : null;
        }

        public static string T(string english)
        {
            if (Language == "en" || string.IsNullOrEmpty(english))
                return english;
            if (_table.TryGetValue(english, out var text))
                return text;
            _missing.TryAdd(english, 0);
            return english;
        }

        public static string T(string english, params object?[] args) => Format(T(english), english, args);

        // Plural text: English uses "one" for 1 and "other" for the rest; the translation is found under the "other" text.
        // {0} is the count, further arguments are {1}, {2} and so on.
        public static string N(long count, string one, string other, params object?[] args)
        {
            object?[] all = new object?[args.Length + 1];
            all[0] = count;
            args.CopyTo(all, 1);
            string english = count == 1 ? one : other;
            if (Language == "en")
                return Format(english, english, all);
            if (_table.TryGetValue(other + FormMark + PluralForm(count), out var form) || _table.TryGetValue(other + FormMark + "other", out form)
                || _table.TryGetValue(other, out form))
                return Format(form, english, all);
            _missing.TryAdd(other, 0);
            return Format(english, english, all);
        }

        // Keeps numbers, sizes, key combinations, file names and player or map names in left-to-right order inside
        // right-to-left text: "1920 × 1080" would otherwise show as "1080 × 1920" and "[rank] map" as "rank] map]".
        // Puts a left-to-right mark (U+200E) on both ends in Arabic and returns the text unchanged in English.
        // (WPF ignores the embedding characters U+202A..U+202E, so marks are the reliable way.)
        public static string Ltr(string text) => IsRightToLeft && text.Length > 0 ? LtrMark + text + LtrMark : text;

        public const char LtrMark = '\u200E';

        // "A", "A and B", "A, B and C"
        public static string List(IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count <= 1)
                return list.FirstOrDefault() ?? "";
            return string.Join(T(", "), list.Take(list.Count - 1)) + T(" and ") + list[^1];
        }

        public static MessageBoxOptions MessageBoxOptions => IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;

        // CLDR plural categories
        private static string PluralForm(long n)
        {
            if (Language != "ar")
                return n == 1 ? "one" : "other";
            long r = Math.Abs(n) % 100;
            return n switch
            {
                0 => "zero",
                1 => "one",
                2 => "two",
                _ when r is >= 3 and <= 10 => "few",
                _ when r is >= 11 and <= 99 => "many",
                _ => "other",
            };
        }

        // A translation with a broken placeholder falls back to the English text instead of failing
        private static string Format(string text, string english, object?[] args)
        {
            try
            {
                return string.Format(Culture, text, args);
            }
            catch (FormatException)
            {
                try { return string.Format(Culture, english, args); }
                catch (FormatException) { return english; }
            }
        }

        private static CultureInfo ArabicCulture()
        {
            try
            {
                var culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-EG").Clone();
                culture.NumberFormat = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
                if (culture.DateTimeFormat.Calendar is not GregorianCalendar)
                    culture.DateTimeFormat.Calendar = new GregorianCalendar();
                return culture;
            }
            catch (Exception)
            {
                return CultureInfo.CurrentCulture;
            }
        }

        // Plain entries are strings; plural entries are objects with zero, one, two, few, many and other
        private static FrozenDictionary<string, string> Load(string language)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using Stream? stream = typeof(Loc).Assembly.GetManifestResourceStream($"Localization.{language}.json");
                if (stream == null)
                    return FrozenDictionary<string, string>.Empty;
                using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String)
                    {
                        if (entry.Value.GetString() is { Length: > 0 } text)
                            table[entry.Name] = text;
                    }
                    else if (entry.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var form in entry.Value.EnumerateObject())
                            if (form.Value.ValueKind == JsonValueKind.String && form.Value.GetString() is { Length: > 0 } text)
                                table[entry.Name + FormMark + form.Name] = text;
                    }
                }
            }
            catch (Exception ex)
            {
                // A broken file leaves the program in English rather than stopping it
                System.Diagnostics.Debug.WriteLine($"Localization/{language}.json: {ex.Message}");
                _missing.TryAdd($"!! Localization/{language}.json could not be read: {ex.Message}", 0);
            }
            return table.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    // {l:T Play online}, {l:T 'Text with a {0} at the start'} or {l:T Text='...'}.
    // A comma ends an argument in XAML markup, so "{l:T Hello, world}" arrives in parts and is joined again with ", ".
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TExtension : MarkupExtension
    {
        public TExtension() { }
        public TExtension(string text) => Text = text;
        public TExtension(string a, string b) => Text = a + ", " + b;
        public TExtension(string a, string b, string c) => Text = a + ", " + b + ", " + c;
        public TExtension(string a, string b, string c, string d) => Text = a + ", " + b + ", " + c + ", " + d;

        public string Text { get; set; } = "";

        public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
    }
}
