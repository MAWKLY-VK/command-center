using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using CommandCenter.Services;

// Builds the map library from a folder of downloaded maps:
//   maps/<id>.zip      one map each, inside a folder named like the map (the way the game wants it)
//   previews/<id>.png  the map's own preview picture
//   catalog.json       what Command Center reads
//   EXCLUDED.md        every map that was left out and why
//
// Usage: MapLibraryBuilder <source maps folder> <game folder> <output folder>
// The source folder is only read. Executables, documents and other stray files are never packed.

if (args.Length < 3)
{
    Console.WriteLine("Usage: MapLibraryBuilder <source maps folder> <game folder> <output folder>");
    return 1;
}

string source = Path.GetFullPath(args[0]);
string game = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);

var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".map", ".tga", ".ini", ".str", ".txt", ".scb", ".wak" };
// Only notes about sharing count; "don't edit without my permission" is common and does not forbid sharing the map as it is
var licenceWords = new Regex(@"(do\s*n[o']?t|may\s+not|must\s+not|not\s+allowed\s+to|no\s+permission\s+to)\s+(re-?)?(distribut|upload|host|share|post|mirror)"
    + @"|(re-?)?(distribut|upload|host|mirror|post)\w*\s+(\S+\s+){0,6}?without\s+(my|the\s+author'?s?|written|prior)\s+permission",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

// Maps that ship with the game are not repacked
var official = new GameFiles(game, communityPatch: false).List(@"Maps\")
    .Where(p => p.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
    .Select(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant())
    .ToHashSet();
Console.WriteLine($"{official.Count} official maps to skip");

// Player counts and start positions from every MapCache.ini the game left in the source tree
var cache = new Dictionary<string, MapCatalog.GameCacheEntry>();
foreach (string file in Directory.EnumerateFiles(source, "MapCache.ini", SearchOption.AllDirectories))
    foreach (var (key, entry) in MapCatalog.ReadMapCache(file))
        cache.TryAdd(key, entry);
Console.WriteLine($"{cache.Count} maps known from MapCache.ini");

var excluded = new List<(string Path, string Reason)>();
var byHash = new Dictionary<string, Candidate>();

foreach (string mapFile in Directory.EnumerateFiles(source, "*.map", SearchOption.AllDirectories).OrderBy(f => f.Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
{
    string relative = Path.GetRelativePath(source, mapFile);
    string stem = Path.GetFileNameWithoutExtension(mapFile);
    string folder = Path.GetDirectoryName(mapFile)!;

    if (official.Contains(stem.ToLowerInvariant()))
    {
        excluded.Add((relative, "ships with the game"));
        continue;
    }

    byte[] mapBytes = File.ReadAllBytes(mapFile);
    string mapHash = Convert.ToHexString(SHA256.HashData(mapBytes));
    if (byHash.TryGetValue(mapHash, out var twin))
    {
        excluded.Add((relative, $"same file as {twin.Relative}"));
        continue;
    }

    // A folder that holds one map gives all its map files; a shared folder only gives the files named after this map
    var mapsHere = Directory.GetFiles(folder, "*.map");
    var files = Directory.GetFiles(folder)
        .Where(f => allowed.Contains(Path.GetExtension(f)) && !f.EndsWith("~"))
        .Where(f => mapsHere.Length == 1 || Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase))
        .Where(f => !Path.GetFileName(f).Equals("MapCache.ini", StringComparison.OrdinalIgnoreCase))
        .ToList();

    // Read every text the author left (also .doc, .rtf and .html, which are not packed) for "do not redistribute"
    string? licence = null;
    foreach (string doc in Directory.GetFiles(folder).Where(f => Regex.IsMatch(Path.GetExtension(f), @"^\.(txt|doc|rtf|html?|nfo)$", RegexOptions.IgnoreCase)))
    {
        var m = licenceWords.Match(Encoding.Latin1.GetString(File.ReadAllBytes(doc)));
        if (m.Success)
        {
            licence = $"{Path.GetFileName(doc)} says \"{m.Value.Trim()}\"";
            break;
        }
    }
    if (licence != null)
    {
        excluded.Add((relative, "author's note: " + licence));
        continue;
    }

    int players;
    List<double[]> starts;
    if (cache.TryGetValue(Path.GetFileName(mapFile).ToLowerInvariant(), out var cached) && cached.Players > 0)
    {
        players = cached.Players;
        starts = cached.Starts.Select(p => new[] { Math.Round(p.X, 3), Math.Round(p.Y, 3) }).ToList();
    }
    else
    {
        players = MapService.CountPlayers(mapFile);
        starts = new List<double[]>();
    }
    if (players == 0)
    {
        excluded.Add((relative, "no start positions (mission or broken map)"));
        continue;
    }

    byHash[mapHash] = new Candidate(relative, stem, folder, files, players, starts,
        files.Any(f => Path.GetFileName(f).Equals("map.ini", StringComparison.OrdinalIgnoreCase) && HasRules(f)),
        files.Any(f => f.EndsWith(".scb", StringComparison.OrdinalIgnoreCase)));
}

// Two different maps with the same name would land in the same folder; the later one gets " (2)", " (3)"...
var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
Directory.CreateDirectory(Path.Combine(output, "maps"));
Directory.CreateDirectory(Path.Combine(output, "previews"));
var catalog = new List<object>();
long total = 0;

foreach (var c in byHash.Values.OrderBy(c => c.Stem, StringComparer.OrdinalIgnoreCase))
{
    string name = c.Stem.Trim();
    int seen = names.TryGetValue(name, out int n) ? n + 1 : 1;
    names[name] = seen;
    if (seen > 1)
        name = $"{name} ({seen})";

    string id = Slug(name);
    string zipPath = Path.Combine(output, "maps", id + ".zip");
    using (var zip = new ZipArchive(new FileStream(zipPath, FileMode.Create), ZipArchiveMode.Create))
    {
        foreach (string file in c.Files)
        {
            // Files named after the map follow its new name; map.ini, map.str and the like keep theirs
            string fileName = Path.GetFileName(file);
            if (Path.GetFileNameWithoutExtension(file).Equals(c.Stem, StringComparison.OrdinalIgnoreCase))
                fileName = name + Path.GetExtension(file).ToLowerInvariant();
            var entry = zip.CreateEntry($"{name}/{fileName}", CompressionLevel.SmallestSize);
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(File.ReadAllBytes(file));
        }
    }
    byte[] zipBytes = File.ReadAllBytes(zipPath);
    total += zipBytes.Length;

    string? preview = null;
    string? tga = c.Files.FirstOrDefault(f => f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(f).Equals(c.Stem, StringComparison.OrdinalIgnoreCase))
                  ?? c.Files.FirstOrDefault(f => f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
    if (tga != null && TgaImage.Load(tga) is { } image)
    {
        preview = $"previews/{id}.png";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = File.Create(Path.Combine(output, "previews", id + ".png"));
        encoder.Save(png);
    }

    catalog.Add(new
    {
        id,
        name,
        players = c.Players,
        sizeBytes = zipBytes.Length,
        sha256 = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant(),
        file = $"maps/{id}.zip",
        preview,
        starts = c.Starts,
        customRules = c.CustomRules,
        scripts = c.Scripts,
    });
}

// Packs from an earlier run that are no longer in the catalog are removed from the output folder
var keep = catalog.Select(m => (string)m.GetType().GetProperty("id")!.GetValue(m)!).ToHashSet();
foreach (string stale in Directory.GetFiles(Path.Combine(output, "maps"), "*.zip").Concat(Directory.GetFiles(Path.Combine(output, "previews"), "*.png")))
    if (!keep.Contains(Path.GetFileNameWithoutExtension(stale)))
        File.Delete(stale);

var json = new { schemaVersion = 1, generated = DateTime.UtcNow.ToString("yyyy-MM-dd"), count = catalog.Count, maps = catalog };
File.WriteAllText(Path.Combine(output, "catalog.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));

var report = new StringBuilder();
report.AppendLine("# Maps left out").AppendLine();
report.AppendLine($"{excluded.Count} map files were not packed.").AppendLine();
foreach (var group in excluded.GroupBy(e => e.Reason.StartsWith("same file") ? "Duplicate" : e.Reason.StartsWith("author") ? "Author's note" : e.Reason))
{
    report.AppendLine($"## {group.Key} ({group.Count()})").AppendLine();
    foreach (var (path, reason) in group.OrderBy(e => e.Path))
        report.AppendLine($"- `{path.Replace('\\', '/')}` — {reason}");
    report.AppendLine();
}
File.WriteAllText(Path.Combine(output, "EXCLUDED.md"), report.ToString());

Console.WriteLine($"{catalog.Count} maps packed, {total / 1024.0 / 1024.0:0.0} MB; {excluded.Count} left out (see EXCLUDED.md)");
return 0;

// A map.ini that only holds comments or blank lines changes nothing
static bool HasRules(string file) =>
    File.ReadLines(file).Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith(';') && !l.TrimStart().StartsWith("//"));

static string Slug(string name)
{
    string slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    if (slug.Length > 48) slug = slug[..48].Trim('-');
    if (slug.Length == 0) slug = "map";
    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..6];
    return $"{slug}-{hash}";
}

sealed record Candidate(string Relative, string Stem, string Folder, List<string> Files, int Players, List<double[]> Starts, bool CustomRules, bool Scripts);
