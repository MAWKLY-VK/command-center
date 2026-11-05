using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CommandCenter.Core.Content
{
    public sealed record MapEntry(string Name, string FolderPath, int Players, long SizeBytes, DecodedImage? Preview);

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

        // What a map zip gives: those files, at most 64 MB each (the largest map in the library is 2.3 MB)
        public static readonly ExtractRules ZipRules = new(name => MapFileTypes.Contains(Path.GetExtension(name)), 64L * 1024 * 1024);

        // Installs every map folder found under a folder or inside a .zip. Returns the installed map names.
        public static List<string> Install(string source)
        {
            try
            {
                var installed = InstallFrom(source);
                OperationLog.Done("install map", source, installed.Count > 0 ? string.Join(", ", installed) : "No map found");
                return installed;
            }
            catch (Exception error)
            {
                OperationLog.Failed("install map", source, error.Message);
                throw;
            }
        }

        // A zip is extracted (and checked) in full before any map is written. Maps already written stay when a later one
        // in the same zip fails.
        private static List<string> InstallFrom(string source)
        {
            string? staging = null;
            try
            {
                string root = source;
                if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    staging = ArchiveExtractor.Extract(source, ZipRules);
                    root = staging;
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
                    if (string.Equals(folder.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && staging != null)
                        name = Path.GetFileNameWithoutExtension(Directory.EnumerateFiles(folder, "*.map").First());
                    if (ArchiveExtractor.NameRefusal(name) is { } reason)
                        throw new InvalidDataException($"\"{name}\" cannot be a map's name: {reason}.");

                    string target = Path.Combine(UserMapsFolder, name);
                    foreach (string file in Directory.EnumerateFiles(folder).Where(f => MapFileTypes.Contains(Path.GetExtension(f))))
                        Place(Path.Combine(target, Path.GetFileName(file)), BoundedFile.Read(file, ZipRules.FileLimit), name);
                    installed.Add(name);
                }

                return installed;
            }
            finally
            {
                if (staging != null)
                    ArchiveExtractor.Discard(staging);
            }
        }

        // A new file is written as it is; one that is already there is backed up first, with its own Undo
        private static void Place(string target, byte[] content, string map)
        {
            if (File.Exists(target))
                BackupService.WriteFile("Map file replaced", target, content, $"{map} · {Path.GetFileName(target)}");
            else
                BackupService.CreateFile(target, content);
        }

        public static void Remove(MapEntry map) =>
            OperationLog.Record("remove map", map.FolderPath, "To the Recycle Bin", () =>
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    map.FolderPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));

        // Multiplayer maps place one waypoint per start position: Player_1_Start, Player_2_Start, ...
        public static int CountPlayers(string mapFile)
        {
            try
            {
                string text = Encoding.ASCII.GetString(RefPack.ReadMap(mapFile));
                return Regex.Matches(text, @"Player_(\d)_Start")
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .Count();
            }
            catch (InvalidDataException ex)
            {
                ErrorLog.Skipped(mapFile, ex.Message);
                return 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    // Maps are stored as "EAR\0" + uncompressed size + a RefPack stream. A stream that declares more than 64 MB, or more
    // than its commands can produce, is refused before anything is allocated for it.
    public static class RefPack
    {
        public const int MaxSize = 64 * 1024 * 1024;

        // The most output one byte of a stream can give: a four-byte command copies at most 1028 bytes
        private const int MostPerByte = 257;

        public static bool IsCompressed(byte[] d) =>
            d.Length > 10 && d[0] == 'E' && d[1] == 'A' && d[2] == 'R' && d[3] == 0;

        // A map file's data, read whole (at most 64 MB) and decompressed when it is compressed
        public static byte[] ReadMap(string path)
        {
            byte[] data = BoundedFile.Read(path, MaxSize);
            return IsCompressed(data) ? Decompress(data) : data;
        }

        public static byte[] Decompress(byte[] d)
        {
            int p = IsCompressed(d) ? 8 : 0;
            Need(d, p, 2);
            int flags = d[p];
            p += 2;
            if ((flags & 0x01) != 0)
                p += (flags & 0x80) != 0 ? 4 : 3;

            int sizeBytes = (flags & 0x80) != 0 ? 4 : 3;
            Need(d, p, sizeBytes);
            long size = 0;
            for (int i = 0; i < sizeBytes; i++)
                size = (size << 8) | d[p + i];
            p += sizeBytes;
            if (size > MaxSize)
                throw new InvalidDataException($"It declares {BoundedFile.Describe(size)} of map data; the most is {BoundedFile.Describe(MaxSize)}.");
            if (size > (long)(d.Length - p) * MostPerByte)
                throw new InvalidDataException("It declares more map data than it holds.");

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
                    Need(d, p, 2);
                    int b1 = d[p + 1];
                    plain = b0 & 3;
                    copy = ((b0 & 0x1C) >> 2) + 3;
                    offset = ((b0 & 0x60) << 3) + b1 + 1;
                    p += 2;
                }
                else if (b0 < 0xC0)
                {
                    Need(d, p, 3);
                    int b1 = d[p + 1], b2 = d[p + 2];
                    plain = (b1 >> 6) & 3;
                    copy = (b0 & 0x3F) + 4;
                    offset = ((b1 & 0x3F) << 8) + b2 + 1;
                    p += 3;
                }
                else if (b0 < 0xE0)
                {
                    Need(d, p, 4);
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
                    Need(d, p, Math.Min(plain, o.Length - q));
                    Array.Copy(d, p, o, q, Math.Min(plain, o.Length - q));
                    break;
                }

                if (q + plain + copy > o.Length || p + plain > d.Length || offset > q + plain)
                    throw Damaged();

                Array.Copy(d, p, o, q, plain);
                p += plain;
                q += plain;
                for (int i = 0; i < copy; i++, q++)
                    o[q] = o[q - offset];
            }

            return o;
        }

        private static void Need(byte[] d, int p, int bytes)
        {
            if (p + bytes > d.Length)
                throw Damaged();
        }

        private static InvalidDataException Damaged() => new("Its map data is damaged.");
    }
}
