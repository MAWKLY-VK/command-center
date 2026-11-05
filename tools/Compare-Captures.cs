#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property Nullable=enable
#:property ImplicitUsings=enable

// Compares two sets of --capture screenshots pixel by pixel, to prove that a change left every page as it was.
//
//     dotnet run --file tools/Compare-Captures.cs -- <before folder> <after folder> [<diff folder>]
//
// Every PNG under <before> is matched with the PNG at the same relative path under <after>. A capture is:
//     same       every pixel matches
//     explained  every differing pixel lies inside a rectangle listed for that capture in <before>\ignore.txt
//     DIFF       anything else, including a different size
//     MISSING    present on one side only
// For each explained or DIFF capture, a diff image (differing pixels in magenta over a dimmed copy of the after
// capture) goes to <diff folder>, which defaults to <after>\diff. The exit code is 0 when nothing is DIFF or MISSING.
//
// ignore.txt holds the areas that change between two runs of the same build (live server numbers, relative times).
// tools/Capture-Pages.ps1 copies tools/capture-ignore.txt there. A path of * applies to every capture:
//     <relative png path or *> <x> <y> <width> <height>   # why this area changes

using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

int exitCode = 2;
// WPF imaging runs on a single-threaded apartment
var worker = new Thread(() => exitCode = Run(args));
worker.SetApartmentState(ApartmentState.STA);
worker.Start();
worker.Join();
return exitCode;

static int Run(string[] args)
{
    if (args.Length is < 2 or > 3)
    {
        Console.Error.WriteLine("Usage: dotnet run --file tools/Compare-Captures.cs -- <before folder> <after folder> [<diff folder>]");
        return 2;
    }

    string before = Path.GetFullPath(args[0]);
    string after = Path.GetFullPath(args[1]);
    string diffs = args.Length == 3 ? Path.GetFullPath(args[2]) : Path.Combine(after, "diff");
    foreach (string folder in new[] { before, after })
    {
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"Folder not found: {folder}");
            return 2;
        }
    }

    Dictionary<string, List<Area>> ignores = ReadIgnores(Path.Combine(before, "ignore.txt"));
    List<string> names = Captures(before).Union(Captures(after), StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    int failing = 0;
    foreach (string name in names)
    {
        string a = Path.Combine(before, name);
        string b = Path.Combine(after, name);
        if (!File.Exists(a) || !File.Exists(b))
        {
            Console.WriteLine($"MISSING    {name} (only {(File.Exists(a) ? "before" : "after")})");
            failing++;
            continue;
        }

        Image old = Load(a);
        Image now = Load(b);
        if (old.Width != now.Width || old.Height != now.Height)
        {
            Console.WriteLine($"DIFF       {name}: size {old.Width}x{old.Height} -> {now.Width}x{now.Height}");
            failing++;
            continue;
        }

        List<Area> allowed = [.. ignores.GetValueOrDefault(Key(name), []), .. ignores.GetValueOrDefault("*", [])];
        bool[] differs = new bool[old.Width * old.Height];
        int count = 0, unexplained = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int i = 0; i < differs.Length; i++)
        {
            int o = i * 4;
            if (old.Pixels[o] == now.Pixels[o] && old.Pixels[o + 1] == now.Pixels[o + 1]
                && old.Pixels[o + 2] == now.Pixels[o + 2] && old.Pixels[o + 3] == now.Pixels[o + 3])
                continue;
            differs[i] = true;
            count++;
            int x = i % old.Width, y = i / old.Width;
            if (allowed.Any(r => r.Contains(x, y)))
                continue;
            unexplained++;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (count == 0)
        {
            Console.WriteLine($"same       {name}");
            continue;
        }

        WriteDiff(Path.Combine(diffs, name), now, differs);
        if (unexplained == 0)
        {
            Console.WriteLine($"explained  {name}: {count:N0} px, all inside ignore.txt areas");
        }
        else
        {
            Console.WriteLine($"DIFF       {name}: {unexplained:N0} px outside ignore.txt areas, within x {minX}-{maxX}, y {minY}-{maxY}");
            failing++;
        }
    }

    Console.WriteLine(failing == 0
        ? $"Result: {names.Count} captures, no unexplained difference."
        : $"Result: {failing} of {names.Count} captures differ. Diff images: {diffs}");
    return failing == 0 ? 0 : 1;
}

// Relative paths of the captures in a folder, leaving out earlier diff images
static IEnumerable<string> Captures(string folder) =>
    Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(folder, path))
        .Where(name => !Key(name).StartsWith("diff/", StringComparison.OrdinalIgnoreCase));

static string Key(string name) => name.Replace('\\', '/');

static Image Load(string path)
{
    using FileStream stream = File.OpenRead(path);
    BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
    byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
    bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
    return new Image(bgra.PixelWidth, bgra.PixelHeight, pixels);
}

static void WriteDiff(string path, Image now, bool[] differs)
{
    byte[] pixels = new byte[now.Pixels.Length];
    for (int i = 0; i < differs.Length; i++)
    {
        int o = i * 4;
        if (differs[i])
        {
            pixels[o] = 255;      // blue
            pixels[o + 1] = 0;    // green
            pixels[o + 2] = 255;  // red: magenta marks a changed pixel
        }
        else
        {
            pixels[o] = (byte)(now.Pixels[o] / 3);
            pixels[o + 1] = (byte)(now.Pixels[o + 1] / 3);
            pixels[o + 2] = (byte)(now.Pixels[o + 2] / 3);
        }
        pixels[o + 3] = 255;
    }

    var bitmap = BitmapSource.Create(now.Width, now.Height, 96, 96, PixelFormats.Bgra32, null, pixels, now.Width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using FileStream file = File.Create(path);
    encoder.Save(file);
}

static Dictionary<string, List<Area>> ReadIgnores(string path)
{
    var areas = new Dictionary<string, List<Area>>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path))
        return areas;
    foreach (string raw in File.ReadAllLines(path))
    {
        string line = raw.Split('#')[0].Trim();
        if (line.Length == 0)
            continue;
        string[] f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5)
            throw new FormatException($"ignore.txt needs '<png> <x> <y> <width> <height>', got: {raw}");
        int[] n = f[1..].Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        string key = Key(f[0]);
        if (!areas.TryGetValue(key, out List<Area>? list))
            areas[key] = list = [];
        list.Add(new Area(n[0], n[1], n[2], n[3]));
    }
    return areas;
}

record Image(int Width, int Height, byte[] Pixels);

record Area(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}
