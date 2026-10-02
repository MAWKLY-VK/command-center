using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandCenter.Services
{
    // Cuts button pictures out of the game's interface atlases, using the MappedImage definitions
    // (texture name plus pixel rectangle) the game itself uses for the control bar.
    public sealed class GameImages
    {
        private sealed record Mapped(string Texture, int Width, int Height, Int32Rect Rect);

        private readonly GameFiles _files;
        private readonly string _language;
        private readonly Dictionary<string, Mapped> _mapped = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BitmapSource?> _textures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BitmapSource?> _images = new(StringComparer.OrdinalIgnoreCase);

        public GameImages(GameFiles files, string language)
        {
            _files = files;
            _language = language;
            var header = new Regex(@"^\s*MappedImage\s+(\S+)", RegexOptions.IgnoreCase);
            var coords = new Regex(@"Left:\s*(-?\d+)\s+Top:\s*(-?\d+)\s+Right:\s*(-?\d+)\s+Bottom:\s*(-?\d+)", RegexOptions.IgnoreCase);

            foreach (string path in files.List(@"Data\INI\MappedImages\").Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
            {
                string? name = null, texture = null;
                int width = 512, height = 512;
                Int32Rect rect = Int32Rect.Empty;
                foreach (string raw in files.ReadText(path).Split('\n'))
                {
                    string line = raw.Split(';')[0].Trim();
                    var m = header.Match(line);
                    if (m.Success)
                    {
                        name = m.Groups[1].Value;
                        texture = null;
                        rect = Int32Rect.Empty;
                        continue;
                    }
                    if (name == null)
                        continue;
                    if (line.Equals("End", StringComparison.OrdinalIgnoreCase))
                    {
                        if (texture != null && !rect.IsEmpty)
                            _mapped.TryAdd(name, new Mapped(texture, width, height, rect));
                        name = null;
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq < 0)
                        continue;
                    string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                    if (key.Equals("Texture", StringComparison.OrdinalIgnoreCase)) texture = value;
                    else if (key.Equals("TextureWidth", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out width);
                    else if (key.Equals("TextureHeight", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out height);
                    else if (key.Equals("Coords", StringComparison.OrdinalIgnoreCase) && coords.Match(value) is { Success: true } c)
                    {
                        int l = Int(c.Groups[1].Value), t = Int(c.Groups[2].Value), r = Int(c.Groups[3].Value), b = Int(c.Groups[4].Value);
                        rect = new Int32Rect(l, t, Math.Max(1, r - l), Math.Max(1, b - t));
                    }
                }
            }
        }

        private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        public BitmapSource? Get(string? mappedImage)
        {
            if (string.IsNullOrEmpty(mappedImage))
                return null;
            if (_images.TryGetValue(mappedImage, out var cached))
                return cached;

            BitmapSource? result = null;
            try
            {
                if (_mapped.TryGetValue(mappedImage, out var map) && Texture(map.Texture) is { } atlas)
                {
                    // Coordinates are given for the declared texture size; scale if the file differs
                    double sx = atlas.PixelWidth / (double)map.Width, sy = atlas.PixelHeight / (double)map.Height;
                    var rect = new Int32Rect((int)(map.Rect.X * sx), (int)(map.Rect.Y * sy), (int)(map.Rect.Width * sx), (int)(map.Rect.Height * sy));
                    rect.Width = Math.Min(rect.Width, atlas.PixelWidth - rect.X);
                    rect.Height = Math.Min(rect.Height, atlas.PixelHeight - rect.Y);
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        var crop = new CroppedBitmap(atlas, rect);
                        crop.Freeze();
                        result = crop;
                    }
                }
            }
            catch
            {
                result = null;
            }
            _images[mappedImage] = result;
            return result;
        }

        private BitmapSource? Texture(string name)
        {
            if (_textures.TryGetValue(name, out var cached))
                return cached;

            string stem = System.IO.Path.GetFileNameWithoutExtension(name);
            BitmapSource? image = null;
            foreach (string folder in new[] { $@"Data\{_language}\Art\Textures\", @"Art\Textures\", @"Data\English\Art\Textures\" })
            {
                if (_files.Read(folder + stem + ".dds") is { } dds && (image = Dds.Decode(dds)) != null)
                    break;
                if (_files.Read(folder + stem + ".tga") is { } tga && (image = TgaImage.Decode(tga, keepAlpha: true)) != null)
                    break;
            }
            _textures[name] = image;
            return image;
        }
    }

    // DXT1, DXT3 and DXT5 textures, the only compressed formats the game loads.
    public static class Dds
    {
        public static BitmapSource? Decode(byte[] d)
        {
            try
            {
                if (d.Length < 128 || Encoding.ASCII.GetString(d, 0, 4) != "DDS ")
                    return null;
                int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
                string fourcc = Encoding.ASCII.GetString(d, 84, 4);
                if (fourcc is not ("DXT1" or "DXT3" or "DXT5"))
                    return null;

                byte[] px = new byte[w * h * 4];
                int p = 128, block = fourcc == "DXT1" ? 8 : 16;
                var col = new (int R, int G, int B, int A)[4];
                for (int by = 0; by < h; by += 4)
                for (int bx = 0; bx < w; bx += 4, p += block)
                {
                    int c = fourcc == "DXT1" ? p : p + 8;
                    ushort c0 = BitConverter.ToUInt16(d, c), c1 = BitConverter.ToUInt16(d, c + 2);
                    uint bits = BitConverter.ToUInt32(d, c + 4);
                    var a = Rgb565(c0);
                    var b = Rgb565(c1);
                    col[0] = (a.R, a.G, a.B, 255);
                    col[1] = (b.R, b.G, b.B, 255);
                    if (c0 > c1 || fourcc != "DXT1")
                    {
                        col[2] = ((2 * a.R + b.R) / 3, (2 * a.G + b.G) / 3, (2 * a.B + b.B) / 3, 255);
                        col[3] = ((a.R + 2 * b.R) / 3, (a.G + 2 * b.G) / 3, (a.B + 2 * b.B) / 3, 255);
                    }
                    else
                    {
                        col[2] = ((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2, 255);
                        col[3] = (0, 0, 0, 0);
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx + i % 4, y = by + i / 4;
                        if (x >= w || y >= h) continue;
                        var k = col[(bits >> (2 * i)) & 3];
                        int o = (y * w + x) * 4;
                        px[o] = (byte)k.B; px[o + 1] = (byte)k.G; px[o + 2] = (byte)k.R; px[o + 3] = (byte)k.A;
                    }
                }
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static (int R, int G, int B) Rgb565(ushort v) => (((v >> 11) & 31) * 255 / 31, ((v >> 5) & 63) * 255 / 63, (v & 31) * 255 / 31);
    }
}
