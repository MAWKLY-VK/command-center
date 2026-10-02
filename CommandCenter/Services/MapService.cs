using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace CommandCenter.Services
{
    public sealed record MapEntry(string Name, string FolderPath, int Players, long SizeBytes, BitmapSource? Preview);

    public static class MapService
    {
        public static string UserMapsFolder => GamePaths.Maps;

        // Every map lives in its own folder: Maps\<name>\<name>.map plus an optional <name>.tga preview.
        public static List<MapEntry> Scan(string root, int maxCount = int.MaxValue, string? preferPrefix = null)
        {
            var maps = new List<MapEntry>();
            if (!Directory.Exists(root))
                return maps;

            var folders = Directory.EnumerateDirectories(root)
                .OrderBy(d => preferPrefix != null && Path.GetFileName(d).StartsWith(preferPrefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

            foreach (string folder in folders)
            {
                if (maps.Count >= maxCount)
                    break;

                string? mapFile = Directory.EnumerateFiles(folder, "*.map").FirstOrDefault();
                if (mapFile == null)
                    continue;

                string preview = Path.ChangeExtension(mapFile, ".tga");
                if (!File.Exists(preview))
                    preview = Directory.EnumerateFiles(folder, "*.tga").FirstOrDefault() ?? "";

                long size = Directory.EnumerateFiles(folder).Sum(f => new FileInfo(f).Length);
                maps.Add(new MapEntry(
                    Path.GetFileName(folder),
                    folder,
                    CountPlayers(mapFile),
                    size,
                    preview.Length > 0 ? TgaImage.Load(preview) : null));
            }

            return maps;
        }

        // Only the files a map folder normally holds are copied.
        private static readonly HashSet<string> MapFileTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ".map", ".tga", ".ini", ".str", ".txt", ".scb", ".wak",
        };

        // Installs every map folder found under a folder or inside a .zip. Returns the installed map names.
        public static List<string> Install(string source)
        {
            string? temp = null;
            try
            {
                string root = source;
                if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    temp = Path.Combine(Path.GetTempPath(), "GOLauncherMap-" + Guid.NewGuid().ToString("N"));
                    ExtractZip(source, temp);
                    root = temp;
                }
                else if (!Directory.Exists(source))
                {
                    return new List<string>();
                }

                var installed = new List<string>();
                var mapFolders = Directory.EnumerateFiles(root, "*.map", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (string folder in mapFolders.OfType<string>())
                {
                    string name = Path.GetFileName(folder);
                    if (string.Equals(folder.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && temp != null)
                        name = Path.GetFileNameWithoutExtension(Directory.EnumerateFiles(folder, "*.map").First());

                    string target = Path.Combine(UserMapsFolder, name);
                    Directory.CreateDirectory(target);
                    foreach (string file in Directory.EnumerateFiles(folder).Where(f => MapFileTypes.Contains(Path.GetExtension(f))))
                        File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                    installed.Add(name);
                }

                return installed;
            }
            finally
            {
                if (temp != null)
                {
                    try { Directory.Delete(temp, recursive: true); } catch { }
                }
            }
        }

        // Extracts only map files and refuses entries that would land outside the target folder.
        private static void ExtractZip(string zipPath, string target)
        {
            string fullTarget = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || !MapFileTypes.Contains(Path.GetExtension(entry.Name)))
                    continue;
                string destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        public static void Remove(MapEntry map)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                map.FolderPath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        // Multiplayer maps place one waypoint per start position: Player_1_Start, Player_2_Start, ...
        public static int CountPlayers(string mapFile)
        {
            try
            {
                byte[] data = File.ReadAllBytes(mapFile);
                if (RefPack.IsCompressed(data))
                    data = RefPack.Decompress(data);

                string text = Encoding.ASCII.GetString(data);
                return Regex.Matches(text, @"Player_(\d)_Start")
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .Count();
            }
            catch
            {
                return 0;
            }
        }
    }

    // Maps are stored as "EAR\0" + uncompressed size + a RefPack stream.
    public static class RefPack
    {
        public static bool IsCompressed(byte[] d) =>
            d.Length > 10 && d[0] == 'E' && d[1] == 'A' && d[2] == 'R' && d[3] == 0;

        public static byte[] Decompress(byte[] d)
        {
            int p = IsCompressed(d) ? 8 : 0;
            int flags = d[p];
            p += 2;
            if ((flags & 0x01) != 0)
                p += (flags & 0x80) != 0 ? 4 : 3;

            int size;
            if ((flags & 0x80) != 0)
            {
                size = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
                p += 4;
            }
            else
            {
                size = (d[p] << 16) | (d[p + 1] << 8) | d[p + 2];
                p += 3;
            }

            byte[] o = new byte[size];
            int q = 0;
            while (p < d.Length)
            {
                int b0 = d[p];
                int plain;
                int copy = 0;
                int offset = 0;

                if (b0 < 0x80)
                {
                    int b1 = d[p + 1];
                    plain = b0 & 3;
                    copy = ((b0 & 0x1C) >> 2) + 3;
                    offset = ((b0 & 0x60) << 3) + b1 + 1;
                    p += 2;
                }
                else if (b0 < 0xC0)
                {
                    int b1 = d[p + 1], b2 = d[p + 2];
                    plain = (b1 >> 6) & 3;
                    copy = (b0 & 0x3F) + 4;
                    offset = ((b1 & 0x3F) << 8) + b2 + 1;
                    p += 3;
                }
                else if (b0 < 0xE0)
                {
                    int b1 = d[p + 1], b2 = d[p + 2], b3 = d[p + 3];
                    plain = b0 & 3;
                    copy = ((b0 & 0x0C) << 6) + b3 + 5;
                    offset = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1;
                    p += 4;
                }
                else if (b0 < 0xFC)
                {
                    plain = ((b0 & 0x1F) << 2) + 4;
                    p += 1;
                }
                else
                {
                    plain = b0 & 3;
                    p += 1;
                    Array.Copy(d, p, o, q, Math.Min(plain, o.Length - q));
                    break;
                }

                if (q + plain + copy > o.Length || p + plain > d.Length || offset > q + plain)
                    throw new InvalidDataException("Corrupt RefPack stream");

                Array.Copy(d, p, o, q, plain);
                p += plain;
                q += plain;
                for (int i = 0; i < copy; i++, q++)
                    o[q] = o[q - offset];
            }

            return o;
        }
    }
}
