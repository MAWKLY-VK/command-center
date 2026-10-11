using System.IO;
using System.Text;

namespace CommandCenter.Services
{
    // The game's Options.ini ("Key = Value" lines). Writes go through BackupService so they can be undone.
    public static class OptionsFile
    {
        // Latin-1 maps every byte to one character and back, so lines in any code page are written back unchanged
        private static readonly Encoding Raw = Encoding.Latin1;

        // A key that appears twice keeps its last value, as in the game
        public static Dictionary<string, string> Read() =>
            File.Exists(GamePaths.Options) ? Parse(File.ReadAllLines(GamePaths.Options)) : new(StringComparer.OrdinalIgnoreCase);

        internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                    values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return values;
        }

        public static string? Get(string key) => Read().TryGetValue(key, out var v) ? v : null;

        // The file with the given keys set: every other line, the line endings and the encoding stay as they were
        public static byte[] With(IDictionary<string, string> changes) =>
            With(File.Exists(GamePaths.Options) ? File.ReadAllBytes(GamePaths.Options) : Array.Empty<byte>(), changes);

        internal static byte[] With(byte[] current, IDictionary<string, string> changes)
        {
            string text = Raw.GetString(current);
            string newline = text.Contains("\r\n") || !text.Contains('\n') ? "\r\n" : "\n";
            var lines = text.Length == 0 ? new List<string>() : text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            foreach (var (key, value) in changes)
            {
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq > 0 && lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key} = {value}";
                        found = true;
                    }
                }
                if (!found)
                    lines.Add($"{key} = {value}");
            }
            return Raw.GetBytes(string.Join(newline, lines) + newline);
        }

        public static bool WouldChange(IDictionary<string, string> changes) =>
            changes.Count > 0 && !(File.Exists(GamePaths.Options) && File.ReadAllBytes(GamePaths.Options).AsSpan().SequenceEqual(With(changes)));

        public static BackupEntry Write(IDictionary<string, string> changes, string title, string detail = "") =>
            BackupService.WriteFile(title, GamePaths.Options, With(changes),
                detail.Length > 0 ? detail : string.Join(", ", changes.Select(c => $"{c.Key} = {c.Value}")));
    }
}
