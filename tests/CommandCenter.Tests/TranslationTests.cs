using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommandCenter.Tests
{
    // The Arabic file is read the way the program reads it (the embedded copy), and every entry is checked
    public class TranslationTests
    {
        private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}");

        private static List<(string Key, JsonElement Value)> Entries()
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream("Localization.ar.json")
                               ?? throw new InvalidOperationException("Localization.ar.json is not embedded");
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.Clone())).ToList();
        }

        private static HashSet<string> Numbers(string text) => Placeholder.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

        [Fact]
        public void Each_text_is_translated_once()
        {
            var twice = Entries().GroupBy(e => e.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(twice.Count == 0, "Translated more than once: " + string.Join(" | ", twice));
        }

        [Fact]
        public void Translations_keep_the_placeholders_of_the_English()
        {
            var wrong = new List<string>();
            foreach (var (key, value) in Entries())
            {
                var english = Numbers(key);
                if (value.ValueKind == JsonValueKind.String)
                {
                    string text = value.GetString() ?? "";
                    if (text.Length > 0 && !Numbers(text).SetEquals(english))
                        wrong.Add(key);
                }
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    // A plural form may leave out the count ("one archive"), but every other value has to be there
                    foreach (var form in value.EnumerateObject())
                    {
                        var numbers = Numbers(form.Value.GetString() ?? "");
                        if (!numbers.IsSubsetOf(english) || !english.Where(n => n != "0").All(numbers.Contains))
                            wrong.Add($"{key} [{form.Name}]");
                    }
                }
            }
            Assert.True(wrong.Count == 0, "Placeholders differ: " + string.Join(" | ", wrong));
        }

        [Fact]
        public void Translations_can_be_formatted()
        {
            var args = Enumerable.Range(0, 10).Select(i => (object)("v" + i)).ToArray();
            var broken = new List<string>();
            foreach (var (key, value) in Entries())
            {
                var texts = value.ValueKind == JsonValueKind.Object
                    ? value.EnumerateObject().Select(f => f.Value.GetString() ?? "")
                    : new[] { value.GetString() ?? "" };
                foreach (string text in texts.Where(t => Placeholder.IsMatch(t)))
                {
                    try { string.Format(text, args); }
                    catch (FormatException) { broken.Add(key); }
                }
            }
            Assert.True(broken.Count == 0, "Cannot be formatted: " + string.Join(" | ", broken));
        }

        [Fact]
        public void Plural_forms_use_known_names()
        {
            string[] known = { "zero", "one", "two", "few", "many", "other" };
            var unknown = Entries().Where(e => e.Value.ValueKind == JsonValueKind.Object)
                .SelectMany(e => e.Value.EnumerateObject().Where(f => !known.Contains(f.Name)).Select(f => $"{e.Key} [{f.Name}]"))
                .ToList();
            Assert.True(unknown.Count == 0, "Unknown plural forms: " + string.Join(" | ", unknown));
        }
    }
}
