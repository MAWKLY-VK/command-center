using System.IO;
using System.Text;

namespace CommandCenter.Core.Imaging
{
    // The textures the hotkey letters feature writes (Imaging/IconLettersService.cs) carry a marker, so the game's own
    // picture can always be found again: this recognizes them and finds the file one of them replaced.
    public static class IconLetterFiles
    {
        public const string Marker = "CCHotkeyLetters";

        public static bool IsGenerated(byte[] data)
        {
            if (data.Length >= Dds.HeaderSize && Encoding.ASCII.GetString(data, 0, 4) == "DDS ")
                return Encoding.ASCII.GetString(data, 60, Marker.Length) == Marker;
            return data.Length >= 18 + Marker.Length && data[0] == Marker.Length && Encoding.ASCII.GetString(data, 18, Marker.Length) == Marker;
        }

        public static bool IsGeneratedFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                byte[] head = new byte[Dds.HeaderSize];
                int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                return IsGenerated(head[..read]);
            }
            catch
            {
                return false;
            }
        }

        // The file one of ours replaced, when there was one before the launcher first wrote there
        public static byte[]? ReplacedFile(string target)
        {
            var first = BackupService.Load().Where(e => e.Kind == "file" && string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.When).FirstOrDefault();
            try
            {
                return first?.BackupFile is { } backup && File.Exists(backup) ? File.ReadAllBytes(backup) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
