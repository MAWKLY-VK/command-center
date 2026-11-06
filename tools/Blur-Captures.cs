#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property Nullable=enable
#:property ImplicitUsings=enable

// Writes a heavily blurred copy of every --capture screenshot, for wpf-ui-review's blur test: with the details gone,
// a screen should show one focal point (PLAY on Home) and a clear order of what matters.
//
//     dotnet run --file tools/Blur-Captures.cs -- <captures folder> [<out folder>]
//
// Every PNG under <captures> (except an earlier blur or diff folder) gets a copy at the same relative path under
// <out>, which defaults to <captures>\blur. The blur is three passes of a box blur in each direction, about as strong
// as squinting at the screen from a step back.

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
    if (args.Length < 1 || !Directory.Exists(args[0]))
    {
        Console.Error.WriteLine("Usage: dotnet run --file tools/Blur-Captures.cs -- <captures folder> [<out folder>]");
        return 2;
    }
    string root = Path.GetFullPath(args[0]);
    string output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(root, "blur"));
    int count = 0;
    foreach (string file in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(root, file);
        string first = relative.Split(Path.DirectorySeparatorChar)[0];
        if (first.Equals("blur", StringComparison.OrdinalIgnoreCase) || first.Equals("diff", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith(output, StringComparison.OrdinalIgnoreCase))
            continue;
        var (width, height, pixels) = Load(file);
        int radius = Math.Max(2, width / 60);
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(pixels, width, height, radius, horizontal: true);
            BoxBlur(pixels, width, height, radius, horizontal: false);
        }
        string target = Path.Combine(output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Save(target, width, height, pixels);
        count++;
    }
    Console.WriteLine($"{count} blurred copies in {output}");
    return 0;
}

static (int Width, int Height, byte[] Pixels) Load(string path)
{
    using var stream = File.OpenRead(path);
    BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
    var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
    bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
    return (bgra.PixelWidth, bgra.PixelHeight, pixels);
}

static void Save(string path, int width, int height, byte[] pixels)
{
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

// A running average over 2 * radius + 1 pixels along one direction, edges repeated
static void BoxBlur(byte[] pixels, int width, int height, int radius, bool horizontal)
{
    int lines = horizontal ? height : width;
    int length = horizontal ? width : height;
    int step = horizontal ? 4 : width * 4;
    var line = new int[length * 4];
    int window = 2 * radius + 1;
    for (int l = 0; l < lines; l++)
    {
        int start = horizontal ? l * width * 4 : l * 4;
        for (int i = 0; i < length; i++)
            for (int c = 0; c < 4; c++)
                line[i * 4 + c] = pixels[start + i * step + c];
        for (int c = 0; c < 4; c++)
        {
            int sum = 0;
            for (int i = -radius; i <= radius; i++)
                sum += line[Math.Clamp(i, 0, length - 1) * 4 + c];
            for (int i = 0; i < length; i++)
            {
                pixels[start + i * step + c] = (byte)(sum / window);
                sum += line[Math.Min(i + radius + 1, length - 1) * 4 + c] - line[Math.Max(i - radius, 0) * 4 + c];
            }
        }
    }
}
