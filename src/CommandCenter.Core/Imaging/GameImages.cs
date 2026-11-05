using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CommandCenter.Core.Imaging
{
    // A MappedImage: a picture inside a texture atlas. Coords are in the declared texture size; Rotated pictures
    // are stored turned 90 degrees clockwise and the game turns them back when drawing.
    public sealed record MappedImage(string Name, string Texture, int TextureWidth, int TextureHeight, ImageRect Coords, bool Rotated);

    // The texture file the game would load for a texture name: its path (relative to the game folder, or full for
    // the user's own Textures folder), its format and its bytes.
    public sealed record TextureFile(string Path, bool IsDds, byte[] Data);

    // Finds button pictures in the game's interface atlases, using the MappedImage definitions (texture name plus
    // pixel rectangle) the game itself uses for the control bar. The App cuts them out (Imaging/GameImageBitmaps.cs).
    //
    // MappedImages (ImageCollection::load): the user's own INI\MappedImages folder first, then
    // Data\INI\MappedImages\TextureSize_512 and Data\INI\MappedImages\HandCreated; in each folder its own files
    // first, then those in sub folders, sorted by name. A later definition of a name overwrites what it sets.
    //
    // Textures (GameFileClass::Set_Name, TextureLoadTaskClass): interface pictures may be compressed, so the .dds
    // of the name is looked for before the .tga, each in Data\<language>\Art\Textures, then Art\Textures, then the
    // user's Textures folder; a loose file wins over an archive at the same path.
    public sealed class GameImages
    {
        private sealed class Definition
        {
            public string? Texture;
            public int Width, Height;
            public ImageRect Coords = ImageRect.Empty;
            public int CoordsWidth, CoordsHeight;
            public bool Rotated;
        }

        private readonly GameFiles _files;
        private readonly string _language;
        private readonly Dictionary<string, MappedImage> _mapped = new(StringComparer.OrdinalIgnoreCase);

        public GameImages(GameFiles files, string language)
        {
            _files = files;
            _language = language;

            var definitions = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);
            string userFolder = Path.Combine(GamePaths.UserData, @"INI\MappedImages");
            if (Directory.Exists(userFolder))
            {
                var userFiles = Directory.EnumerateFiles(userFolder, "*.ini", SearchOption.AllDirectories).ToList();
                foreach (string path in InLoadOrder(userFiles, userFolder))
                    Parse(SafeRead(path), definitions);
            }
            foreach (string folder in new[] { @"Data\INI\MappedImages\TextureSize_512\", @"Data\INI\MappedImages\HandCreated\" })
            {
                var paths = files.List(folder).Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (string path in InLoadOrder(paths, folder))
                    Parse(files.ReadText(path), definitions);
            }

            foreach (var (name, d) in definitions)
            {
                if (d.Texture != null && !d.Coords.IsEmpty)
                    _mapped[name] = new MappedImage(name, d.Texture, d.CoordsWidth, d.CoordsHeight, d.Coords, d.Rotated);
            }
        }

        public GameFiles Files => _files;
        public string Language => _language;

        public MappedImage? Find(string? name) => name != null && _mapped.TryGetValue(name, out var m) ? m : null;

        // INI::loadDirectory: the folder's own files first, then the ones in sub folders, each sorted the game's way
        private static IEnumerable<string> InLoadOrder(List<string> paths, string folder)
        {
            string root = folder.TrimEnd('\\') + "\\";
            bool Nested(string p) => p.Length > root.Length && p.IndexOf('\\', root.Length) >= 0;
            return paths.Where(p => !Nested(p)).OrderBy(p => p, GameFiles.EngineOrder)
                .Concat(paths.Where(Nested).OrderBy(p => p, GameFiles.EngineOrder));
        }

        private static string SafeRead(string path)
        {
            try { return Encoding.Latin1.GetString(File.ReadAllBytes(path)); }
            catch { return ""; }
        }

        private static readonly Regex Header = new(@"^\s*MappedImage\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CoordsValue = new(@"Left:\s*(-?\d+)\s+Top:\s*(-?\d+)\s+Right:\s*(-?\d+)\s+Bottom:\s*(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void Parse(string text, Dictionary<string, Definition> definitions)
        {
            Definition? current = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Split(';')[0].Trim();
                var m = Header.Match(line);
                if (m.Success)
                {
                    string name = m.Groups[1].Value;
                    if (!definitions.TryGetValue(name, out current))
                        definitions[name] = current = new Definition { Width = 0, Height = 0 };
                    continue;
                }
                if (current == null)
                    continue;
                if (line.Equals("End", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                if (key.Equals("Texture", StringComparison.OrdinalIgnoreCase))
                {
                    // A name that would lead out of the texture folders is not the game's, and is ignored
                    if (IsPlainTextureName(value))
                        current.Texture = value;
                }
                else if (key.Equals("TextureWidth", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out current.Width);
                else if (key.Equals("TextureHeight", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out current.Height);
                else if (key.Equals("Status", StringComparison.OrdinalIgnoreCase)) current.Rotated = value.Contains("ROTATED_90_CLOCKWISE", StringComparison.OrdinalIgnoreCase);
                else if (key.Equals("Coords", StringComparison.OrdinalIgnoreCase) && CoordsValue.Match(value) is { Success: true } c
                         && Int(c.Groups[1].Value) is { } l && Int(c.Groups[2].Value) is { } t && Int(c.Groups[3].Value) is { } r && Int(c.Groups[4].Value) is { } b)
                {
                    // The game turns coordinates into fractions of the texture size known at this point
                    current.Coords = new ImageRect(l, t, (int)Math.Clamp((long)r - l, 1, int.MaxValue), (int)Math.Clamp((long)b - t, 1, int.MaxValue));
                    current.CoordsWidth = current.Width;
                    current.CoordsHeight = current.Height;
                }
            }
        }

        // Null for a number too large for the game's coordinates
        private static int? Int(string s) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : null;

        // A texture name the game looks up in its texture folders: no drive, no network or rooted path, no ".." step
        internal static bool IsPlainTextureName(string name) =>
            name.Length > 0 && !Path.IsPathRooted(name) && name.IndexOf(':') < 0
            && !name.Split('\\', '/').Any(part => part == "..");

        // Where a picture sits in a texture of the given size: coordinates are fractions of the declared size
        public static ImageRect? PixelRect(MappedImage map, int textureWidth, int textureHeight)
        {
            double sx = map.TextureWidth > 0 ? textureWidth / (double)map.TextureWidth : 1;
            double sy = map.TextureHeight > 0 ? textureHeight / (double)map.TextureHeight : 1;
            int x = (int)Math.Round(map.Coords.X * sx), y = (int)Math.Round(map.Coords.Y * sy);
            int right = (int)Math.Round((map.Coords.X + map.Coords.Width) * sx), bottom = (int)Math.Round((map.Coords.Y + map.Coords.Height) * sy);
            x = Math.Clamp(x, 0, textureWidth);
            y = Math.Clamp(y, 0, textureHeight);
            right = Math.Clamp(right, x, textureWidth);
            bottom = Math.Clamp(bottom, y, textureHeight);
            return right > x && bottom > y ? new ImageRect(x, y, right - x, bottom - y) : null;
        }

        // The pixels of a texture as the game would load it, or null when it is missing or unreadable. Not cached here:
        // the App keeps the bitmaps it makes from them (Imaging/GameImageBitmaps.cs).
        public DecodedImage? TexturePixels(string name)
        {
            DecodedImage? image = null;
            try
            {
                if (ResolveTexture(name) is { } file)
                    image = file.IsDds ? Dds.Decode(file.Data, file.Path) : TgaImage.Decode(file.Data, keepAlpha: true, source: file.Path);
            }
            catch
            {
                image = null;
            }
            return image;
        }

        // The places the game looks for a texture, in its order
        public IEnumerable<(string Path, bool IsDds, bool InUserFolder)> TextureCandidates(string name)
        {
            // DDSFileClass swaps the last three letters for "dds"; the targa loader opens the name as given
            var names = new List<(string File, bool IsDds)> { (name.Length > 3 ? name[..^3] + "dds" : name, true) };
            if (name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                names.Add((name, false));
            string userFolder = Path.Combine(GamePaths.UserData, "Textures");
            foreach (var (file, isDds) in names)
            {
                yield return ($@"Data\{_language}\Art\Textures\{file}", isDds, false);
                yield return ($@"Art\Textures\{file}", isDds, false);
                yield return (Path.Combine(userFolder, file), isDds, true);
            }
        }

        // The file the game would load for a texture. Atlases Command Center wrote itself (hotkey letters) are
        // skipped, so this is always the picture without our letters.
        public TextureFile? ResolveTexture(string name)
        {
            if (!IsPlainTextureName(name))
                return null;
            foreach (var (path, isDds, inUserFolder) in TextureCandidates(name))
            {
                if (inUserFolder)
                {
                    if (File.Exists(path))
                        return GameFiles.ReadLoose(path) is { } own ? new TextureFile(path, isDds, own) : null;
                    continue;
                }

                string loose = _files.LoosePath(path);
                if (File.Exists(loose))
                {
                    if (GameFiles.ReadLoose(loose) is not { } data)
                        return null;
                    if (!IconLetterFiles.IsGenerated(data))
                        return new TextureFile(path, isDds, data);
                    // Ours: the file it replaced, if there was one, otherwise whatever the archives hold here
                    if (IconLetterFiles.ReplacedFile(loose) is { } original)
                        return new TextureFile(path, isDds, original);
                }
                if (_files.ReadFromArchive(path) is { } packed)
                    return new TextureFile(path, isDds, packed);
            }
            return null;
        }
    }

    // DXT1, DXT3 and DXT5 textures, the only formats the game's .dds loader reads.
    public static class Dds
    {
        public const int HeaderSize = 128;

        public static string? FourCC(byte[] d) =>
            d.Length >= HeaderSize && Encoding.ASCII.GetString(d, 0, 4) == "DDS " && Encoding.ASCII.GetString(d, 84, 4) is var f
            && f is "DXT1" or "DXT3" or "DXT5" ? f : null;

        public static int BlockSize(string fourcc) => fourcc == "DXT1" ? 8 : 16;

        // Null when the texture is not one the game reads, or is damaged; a damaged one is written to the error log under
        // source, when given
        public static DecodedImage? Decode(byte[] d, string? source = null)
        {
            try
            {
                if (FourCC(d) is not { } fourcc)
                    return null;
                int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
                return new DecodedImage(w, h, DecodePixels(d, fourcc, w, h, HeaderSize));
            }
            catch (InvalidDataException ex)
            {
                if (source != null)
                    ErrorLog.Skipped(source, ex.Message);
                return null;
            }
            catch
            {
                return null;
            }
        }

        // Straight (not premultiplied) BGRA pixels of the first level. A size beyond the texture limit, or one that needs
        // more blocks than the data holds, is refused before anything is allocated.
        public static byte[] DecodePixels(byte[] d, string fourcc, int w, int h, int offset)
        {
            if (w <= 0 || h <= 0)
                throw new InvalidDataException("The texture's size cannot be read.");
            if (w > ImageLimits.Texture || h > ImageLimits.Texture)
                throw new InvalidDataException($"The texture is {w} x {h}, larger than {ImageLimits.Texture} x {ImageLimits.Texture}.");
            long needed = (long)((w + 3) / 4) * ((h + 3) / 4) * BlockSize(fourcc);
            if (offset < 0 || offset + needed > d.Length)
                throw new InvalidDataException("The texture holds less data than its size needs.");

            byte[] px = new byte[w * h * 4];
            int p = offset, block = BlockSize(fourcc);
            var col = new (int R, int G, int B, int A)[4];
            var alpha = new int[16];
            for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4, p += block)
            {
                if (fourcc == "DXT3")
                {
                    for (int i = 0; i < 16; i++)
                        alpha[i] = ((d[p + i / 2] >> (4 * (i & 1))) & 15) * 17;
                }
                else if (fourcc == "DXT5")
                {
                    int a0 = d[p], a1 = d[p + 1];
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++)
                        bits |= (ulong)d[p + 2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++)
                        alpha[i] = Dxt.AlphaValue(a0, a1, (int)((bits >> (3 * i)) & 7));
                }

                int c = fourcc == "DXT1" ? p : p + 8;
                ushort c0 = BitConverter.ToUInt16(d, c), c1 = BitConverter.ToUInt16(d, c + 2);
                uint indices = BitConverter.ToUInt32(d, c + 4);
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
                    var k = col[(indices >> (2 * i)) & 3];
                    int o = (y * w + x) * 4;
                    px[o] = (byte)k.B; px[o + 1] = (byte)k.G; px[o + 2] = (byte)k.R;
                    px[o + 3] = (byte)(fourcc == "DXT1" ? k.A : alpha[i]);
                }
            }
            return px;
        }

        public static (int R, int G, int B) Rgb565(ushort v) => (((v >> 11) & 31) * 255 / 31, ((v >> 5) & 63) * 255 / 63, (v & 31) * 255 / 31);
    }
}
