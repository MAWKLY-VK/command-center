using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace CommandCenter.Services
{
    public sealed class MapInfo
    {
        public required string Name { get; init; }
        public required string Folder { get; init; }
        public required string MapFile { get; init; }
        public long SizeBytes { get; init; }
        public DateTime Modified { get; init; }
        public string? PreviewPath { get; init; }
        public int Players { get; set; }
        public List<Point> Starts { get; set; } = new();   // 0..1 across the map, top-left origin
        public bool Installed { get; set; }
    }

    // Lists map folders and reads player counts and start positions, first from the game's own
    // MapCache.ini, then from the map file itself (cached so the next start is instant).
    public static class MapCatalog
    {
        private sealed record CacheEntry(long Size, long Ticks, int Players, List<double[]> Starts);

        private static string CachePath => Path.Combine(GamePaths.AppData, "mapinfo.json");
        private static Dictionary<string, CacheEntry>? _cache;
        private static readonly object Lock = new();

        public static List<MapInfo> Scan(string root)
        {
            var maps = new List<MapInfo>();
            if (!Directory.Exists(root))
                return maps;

            var gameCache = ReadMapCache(Path.Combine(root, "MapCache.ini"));
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                string[] files;
                try { files = Directory.GetFiles(folder); }
                catch { continue; }
                string? mapFile = files.FirstOrDefault(f => f.EndsWith(".map", StringComparison.OrdinalIgnoreCase));
                if (mapFile == null)
                    continue;

                var info = new FileInfo(mapFile);
                string? preview = Path.ChangeExtension(mapFile, ".tga");
                if (!File.Exists(preview))
                    preview = files.FirstOrDefault(f => f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));

                var map = new MapInfo
                {
                    Name = Path.GetFileName(folder),
                    Folder = folder,
                    MapFile = mapFile,
                    SizeBytes = files.Sum(f => SafeLength(f)),
                    Modified = info.LastWriteTime,
                    PreviewPath = preview,
                };
                if (gameCache.TryGetValue(Path.GetFileName(mapFile).ToLowerInvariant(), out var cached))
                {
                    map.Players = cached.Players;
                    map.Starts = cached.Starts;
                }
                else if (FromOwnCache(mapFile, info) is { } own)
                {
                    map.Players = own.Players;
                    map.Starts = own.Starts.Select(s => new Point(s[0], s[1])).ToList();
                }
                maps.Add(map);
            }
            return maps;
        }

        private static long SafeLength(string file)
        {
            try { return new FileInfo(file).Length; }
            catch { return 0; }
        }

        // Fills in maps the game has never cached by reading their start waypoints; slow, so it runs in the background
        public static void FillMissing(IEnumerable<MapInfo> maps, Action<MapInfo> updated, CancellationToken ct)
        {
            bool changed = false;
            foreach (var map in maps.Where(m => m.Players == 0))
            {
                if (ct.IsCancellationRequested)
                    break;
                var (players, starts) = ReadStarts(map.MapFile);
                map.Players = players;
                map.Starts = starts;
                var info = new FileInfo(map.MapFile);
                lock (Lock)
                {
                    LoadOwnCache()[map.MapFile.ToLowerInvariant()] = new CacheEntry(info.Length, info.LastWriteTimeUtc.Ticks, players,
                        starts.Select(p => new[] { p.X, p.Y }).ToList());
                }
                changed = true;
                updated(map);
            }
            if (changed)
                SaveOwnCache();
        }

        // ── The game's MapCache.ini ──
        // Keys are the full map path with "_XX" hex escapes, for example "..._5Cmy_20map_5Cmy_20map_2Emap"

        public sealed record GameCacheEntry(int Players, List<Point> Starts);

        public static Dictionary<string, GameCacheEntry> ReadMapCache(string path)
        {
            var result = new Dictionary<string, GameCacheEntry>();
            if (!File.Exists(path))
                return result;
            string? key = null;
            int players = 0;
            double maxX = 0, maxY = 0;
            var starts = new SortedDictionary<int, Point>();
            var coord = new Regex(@"X:\s*(-?[\d.]+)\s+Y:\s*(-?[\d.]+)");

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("MapCache ", StringComparison.OrdinalIgnoreCase))
                {
                    key = Unescape(line[9..].Trim());
                    key = key[(key.LastIndexOf('\\') + 1)..].ToLowerInvariant();
                    players = 0;
                    maxX = maxY = 0;
                    starts.Clear();
                    continue;
                }
                if (key == null)
                    continue;
                if (line.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    var points = maxX > 0 && maxY > 0
                        ? starts.Values.Select(p => new Point(Math.Clamp(p.X / maxX, 0, 1), Math.Clamp(1 - p.Y / maxY, 0, 1))).ToList()
                        : new List<Point>();
                    result[key] = new GameCacheEntry(players, points);
                    key = null;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                string name = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                if (name.Equals("numPlayers", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out players);
                else if (name.Equals("extentMax", StringComparison.OrdinalIgnoreCase) && coord.Match(value) is { Success: true } m)
                {
                    maxX = Num(m.Groups[1].Value);
                    maxY = Num(m.Groups[2].Value);
                }
                else if (Regex.Match(name, @"^Player_(\d)_Start$") is { Success: true } s && coord.Match(value) is { Success: true } c)
                    starts[int.Parse(s.Groups[1].Value)] = new Point(Num(c.Groups[1].Value), Num(c.Groups[2].Value));
            }
            return result;
        }

        private static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

        private static string Unescape(string key)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < key.Length; i++)
            {
                if (key[i] == '_' && i + 2 < key.Length && Uri.IsHexDigit(key[i + 1]) && Uri.IsHexDigit(key[i + 2]))
                {
                    bytes.Add(Convert.ToByte(key.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                {
                    bytes.Add((byte)key[i]);
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        // ── Reading the map file ──
        // Waypoints sit in the map's object list as "waypointName" properties; positions are not needed
        // to count players, and the start markers are left out for maps the game never cached.

        private static (int Players, List<Point> Starts) ReadStarts(string mapFile)
        {
            try
            {
                byte[] data = File.ReadAllBytes(mapFile);
                if (RefPack.IsCompressed(data))
                    data = RefPack.Decompress(data);
                string text = Encoding.ASCII.GetString(data);
                int players = Regex.Matches(text, @"Player_(\d)_Start").Select(m => m.Groups[1].Value).Distinct().Count();
                return (players, new List<Point>());
            }
            catch
            {
                return (0, new List<Point>());
            }
        }

        // ── Our own cache ──

        private static Dictionary<string, CacheEntry> LoadOwnCache()
        {
            if (_cache != null)
                return _cache;
            try
            {
                _cache = File.Exists(CachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath)) ?? new()
                    : new();
            }
            catch
            {
                _cache = new();
            }
            return _cache;
        }

        private static CacheEntry? FromOwnCache(string mapFile, FileInfo info)
        {
            lock (Lock)
            {
                return LoadOwnCache().TryGetValue(mapFile.ToLowerInvariant(), out var e) && e.Size == info.Length && e.Ticks == info.LastWriteTimeUtc.Ticks ? e : null;
            }
        }

        private static void SaveOwnCache()
        {
            try
            {
                Directory.CreateDirectory(GamePaths.AppData);
                lock (Lock)
                    File.WriteAllText(CachePath, JsonSerializer.Serialize(LoadOwnCache()));
            }
            catch { }
        }

        // ── Install and remove ──

        public static string RemovedFolder => Path.Combine(GamePaths.AppData, "Removed maps");

        // Copies one map folder into the user's Maps folder. Returns false when a map with that name is already there.
        public static bool Install(MapInfo map)
        {
            string target = Path.Combine(GamePaths.Maps, map.Name);
            if (Directory.Exists(target))
                return false;
            var installed = MapService.Install(map.Folder);
            return installed.Count > 0;
        }

        // Moves an installed map out of the game's reach; the returned path lets Undo move it back
        public static string Remove(MapInfo map)
        {
            string folder = Path.Combine(RemovedFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, Path.GetFileName(map.Folder));
            Directory.Move(map.Folder, target);
            return target;
        }
    }
}
