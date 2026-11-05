using System.Globalization;

namespace CommandCenter.Core.Text
{
    // The program's wording for counts and lists of names. Numbers are written the way Windows writes them for the
    // player (the current culture).
    public static class English
    {
        // "1 map" or "3 maps". {0} is the count; further values are {1}, {2} and so on.
        public static string Plural(long count, string one, string other, params object?[] args)
        {
            var values = new object?[args.Length + 1];
            values[0] = count;
            args.CopyTo(values, 1);
            return string.Format(CultureInfo.CurrentCulture, count == 1 ? one : other, values);
        }

        // "A", "A and B", "A, B and C". Past show names: "a, b, c and 4 more".
        public static string List(IEnumerable<string> names, int show = int.MaxValue)
        {
            string[] all = names.ToArray();
            if (all.Length <= 1)
                return all.FirstOrDefault() ?? "";
            if (all.Length <= show)
                return string.Join(", ", all[..^1]) + " and " + all[^1];
            return string.Join(", ", all.Take(show)) + " and " + (all.Length - show).ToString(CultureInfo.CurrentCulture) + " more";
        }
    }
}
