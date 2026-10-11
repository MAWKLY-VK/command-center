using System.Globalization;

namespace CommandCenter.Services
{
    // A text written in English and translated where it is shown. The health checks build their texts this way: the
    // screen shows the player's language, while the support report keeps the English that Generals Online's support
    // reads. Arguments that are LocText themselves are translated too; file names and numbers stay as they are.
    public sealed class LocText
    {
        private string Format { get; init; } = "";
        private object?[] Args { get; init; } = Array.Empty<object?>();
        private long? Count { get; init; }
        private string One { get; init; } = "";
        private string? Separator { get; init; }
        private LocText[] Parts { get; init; } = Array.Empty<LocText>();

        private LocText() { }

        public static implicit operator LocText(string text) => new() { Format = text ?? "" };

        public bool IsEmpty => Format.Length == 0 && Parts.Length == 0;

        // "{0} replaces part of the game" with its values
        public static LocText F(string format, params object?[] args) => new() { Format = format, Args = args };

        // A value that is not translated, such as a driver name or an error message from Windows
        public static LocText Value(string text) => F("{0}", text);

        // A count with its singular and plural English; {0} is the count, further arguments are {1}, {2} and so on
        public static LocText N(long count, string one, string other, params object?[] args) =>
            new() { Format = other, One = one, Count = count, Args = args };

        // Sentences shown one after the other ("Missing: a. Damaged: b")
        public static LocText Join(string separator, IEnumerable<LocText> parts) => new() { Separator = separator, Parts = parts.ToArray() };

        // "a, b and c" or "a, b, c and 4 more"
        public static LocText Names(IReadOnlyList<string> names, int show = 3)
        {
            if (names.Count == 1)
                return Value(names[0]);
            if (names.Count <= show)
                return F("{0} and {1}", new NameList(names.Take(names.Count - 1).ToList()), names[^1]);
            return N(names.Count - show, "{1} and {0} more", "{1} and {0} more", new NameList(names.Take(show).ToList()));
        }

        public override string ToString()
        {
            if (Separator != null)
                return string.Join(Separator, Parts.Select(p => p.ToString()));
            var args = ArgsFor(english: true);
            if (Count is { } n)
                return SafeFormat(n == 1 ? One : Format, args.Prepend(n).ToArray());
            return args.Length == 0 ? Format : SafeFormat(Format, args);
        }

        public string Localized
        {
            get
            {
                if (Separator != null)
                    return string.Join(Loc.T(Separator), Parts.Select(p => p.Localized));
                var args = ArgsFor(english: false);
                if (Count is { } n)
                    return Loc.N(n, One, Format, args);
                // A value shown on its own (a driver name, an error message) has nothing to translate
                if (Format == "{0}" && args.Length == 1)
                    return args[0]?.ToString() ?? "";
                return args.Length == 0 ? Loc.T(Format) : Loc.T(Format, args);
            }
        }

        private object?[] ArgsFor(bool english) => Args.Select(a => a switch
        {
            LocText text => english ? text.ToString() : text.Localized,
            NameList list => english ? string.Join(", ", list.Items) : Loc.Join(list.Items.Select(Loc.Embed)),
            string s when !english => Loc.Embed(s),
            _ => a,
        }).ToArray();

        private static string SafeFormat(string format, object?[] args)
        {
            try { return string.Format(CultureInfo.InvariantCulture, format, args); }
            catch (FormatException) { return format; }
        }

        // Names joined with commas in the reading order of the language
        private sealed record NameList(List<string> Items);
    }
}
