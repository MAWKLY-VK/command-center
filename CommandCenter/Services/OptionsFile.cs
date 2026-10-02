using System.IO;
using System.Text;

namespace CommandCenter.Services
{
    // The game's Options.ini ("Key = Value" lines). Writes go through BackupService so they can be undone.
    public static class OptionsFile
    {
        public static Dictionary<string, string> Read()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(GamePaths.Options))
                return values;
            foreach (string line in File.ReadAllLines(GamePaths.Options))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                    values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return values;
        }

        public static string? Get(string key) => Read().TryGetValue(key, out var v) ? v : null;

        public static byte[] With(IDictionary<string, string> changes)
        {
            var lines = File.Exists(GamePaths.Options) ? File.ReadAllLines(GamePaths.Options).ToList() : new List<string>();
            foreach (var (key, value) in changes)
            {
                int index = lines.FindIndex(l => l.Contains('=') && l[..l.IndexOf('=')].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) lines[index] = $"{key} = {value}";
                else lines.Add($"{key} = {value}");
            }
            return Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n");
        }

        public static BackupEntry Write(IDictionary<string, string> changes, string title, string detail = "") =>
            BackupService.WriteFile(title, GamePaths.Options, With(changes),
                detail.Length > 0 ? detail : string.Join(", ", changes.Select(c => $"{c.Key} = {c.Value}")));
    }
}
