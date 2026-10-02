using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandCenter.Services
{
    // A texture with hotkey letters drawn on its button pictures, ready to be written as a loose file
    public sealed record LetteredTexture(string Texture, string RelativePath, byte[] Content, int Icons);

    // Optional: draws each button's hotkey letter on its picture (bottom left), so the control bar shows the keys.
    //
    // How the game finds these pictures:
    //  - A CommandButton's ButtonImage names a MappedImage: a texture file and a rectangle in it (Image.cpp).
    //  - The control bar draws it with WW3DAssetManager::Get_Texture, which allows compression, so the .dds of the name
    //    is tried before the .tga (TextureLoadTaskClass::Begin_Load), and each is looked for in Data\<language>\Art\Textures
    //    first, then Art\Textures, then the user's Textures folder (GameFileClass::Set_Name); a loose file wins over the
    //    archives at the same path (FileSystem::openFile). The .dds loader only reads DXT1, DXT3 and DXT5 (DDSFileClass).
    // So a loose file in Data\<language>\Art\Textures\ always wins when it has the format the game picks today: a .dds
    // in the same DXT format (only the 4x4 blocks under a letter are encoded again, the rest stays as it was), or a
    // 32-bit .tga (lossless). Only atlases that hold button pictures are written.
    //
    // A picture is shared by every button that uses it, so it carries one letter: the key of the buttons using it, and
    // when those differ, the key most of them answer to. Units in the production queue show the same pictures.
    //
    // Each file carries a marker, so the launcher's own pictures and every rebuild start from the game's picture.
    public static class IconLettersService
    {
        public const string Title = "Hotkey letters on icons";

        private const string Marker = "CCHotkeyLetters";

        // On when the setting is on and the game folder still holds our textures
        public static bool IsEnabled => AppSettings.Current.IconLetters && OwnTargets().Any(t => File.Exists(t) && IsGeneratedFile(t));

        // Writes the textures for the keys the game uses now (the saved ones); returns how many pictures got a letter
        public static int Apply(HotkeyService hotkeys)
        {
            var files = hotkeys.Images.Files;
            var built = Build(hotkeys);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var texture in built)
            {
                string target = files.LoosePath(texture.RelativePath);
                written.Add(target);
                if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(texture.Content))
                    continue;
                if (Clear(target))
                    BackupService.WriteFile(Title, target, texture.Content, $"{texture.RelativePath} · {texture.Icons} icons");
            }

            // Textures from an earlier run that are not needed any more (a picture moved to another file, the language changed...)
            foreach (string old in OwnTargets().Where(t => !written.Contains(t)))
                Clear(old);

            AppSettings.Current.IconLetters = true;
            AppSettings.Current.Save();
            return built.Sum(t => t.Icons);
        }

        public static void Remove()
        {
            foreach (string target in OwnTargets())
                Clear(target);

            // Our files the backup list no longer knows about
            foreach (string file in Strays().ToList())
                RecycleBin.SendFile(file);

            AppSettings.Current.IconLetters = false;
            AppSettings.Current.Save();
        }

        // After hotkeys are saved: rebuild the textures when the feature is on
        public static bool RefreshIfEnabled(HotkeyService hotkeys)
        {
            if (!AppSettings.Current.IconLetters)
                return false;
            Apply(hotkeys);
            return true;
        }

        // The buttons' pictures with the letter the game will show, for the keys as they are in the editor (unsaved
        // changes included). Buttons without a picture are left out.
        public static List<BitmapSource> RenderPreview(HotkeyService hotkeys, IEnumerable<HotkeyButton> buttons, double scale = 1) =>
            OnSta(() =>
            {
                var letters = Letters(hotkeys, hotkeys.KeyFor);
                var result = new List<BitmapSource>();
                foreach (var button in buttons)
                {
                    if (hotkeys.Images.Get(button.Image) is not { } icon)
                        continue;
                    char key = button.Image != null && letters.TryGetValue(button.Image, out char k) ? k : '\0';
                    result.Add(Render(icon, key, scale));
                }
                return result;
            });

        // All textures with letters, for the saved keys
        public static List<LetteredTexture> Build(HotkeyService hotkeys)
        {
            var images = hotkeys.Images;
            var csf = CsfFile.Load(images.Files.Read(hotkeys.CsfPath) ?? throw new FileNotFoundException(hotkeys.CsfPath));
            return OnSta(() => BuildTextures(images, Letters(hotkeys, label => CsfFile.HotkeyOf(csf.Get(label)))));
        }

        // ── Letters ──

        // The letter of every picture used by a button. Pictures that are the same piece of the same atlas count as one.
        private static Dictionary<string, char> Letters(HotkeyService hotkeys, Func<string, char> keyOf)
        {
            var votes = new Dictionary<string, Dictionary<char, int>>(StringComparer.OrdinalIgnoreCase);
            void Vote(string? image, char key)
            {
                if (string.IsNullOrEmpty(image))
                    return;
                if (!votes.TryGetValue(image, out var count))
                    votes[image] = count = new Dictionary<char, int>();
                key = HotkeyService.IsValidButtonKey(char.ToUpperInvariant(key)) ? char.ToUpperInvariant(key) : '\0';
                count[key] = count.GetValueOrDefault(key) + 1;
            }
            foreach (var menu in hotkeys.Armies.SelectMany(a => a.Menus))
            {
                foreach (var b in menu.Buttons)
                    Vote(b.Image, keyOf(b.Label));
                foreach (var b in menu.Silent)
                    Vote(b.Image, '\0');
            }

            var result = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
            var images = hotkeys.Images;
            foreach (var group in votes.Keys.GroupBy(n => images.Find(n) is { } m ? $"{m.Texture}|{m.Coords}|{m.TextureWidth}|{m.TextureHeight}" : n, StringComparer.OrdinalIgnoreCase))
            {
                var total = new Dictionary<char, int>();
                foreach (string name in group)
                    foreach (var (key, count) in votes[name])
                        total[key] = total.GetValueOrDefault(key) + count;
                // Most buttons win; on a tie a letter beats none, then the alphabet decides
                char pick = total.OrderByDescending(p => p.Value).ThenBy(p => p.Key == '\0' ? 1 : 0).ThenBy(p => p.Key).First().Key;
                foreach (string name in group)
                    result[name] = pick;
            }
            return result;
        }

        // ── Textures ──

        private static List<LetteredTexture> BuildTextures(GameImages images, Dictionary<string, char> letters)
        {
            var result = new List<LetteredTexture>();
            var byTexture = letters.Where(l => l.Value != '\0')
                .Select(l => (Map: images.Find(l.Key), Key: l.Value))
                .Where(x => x.Map != null)
                .GroupBy(x => x.Map!.Texture, StringComparer.OrdinalIgnoreCase);

            foreach (var group in byTexture)
            {
                try
                {
                    if (BuildTexture(images, group.Key, group.Select(x => (x.Map!, x.Key))) is { } texture)
                        result.Add(texture);
                }
                catch
                {
                    // A texture we cannot read stays as the game has it
                }
            }
            return result.OrderBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static LetteredTexture? BuildTexture(GameImages images, string texture, IEnumerable<(MappedImage Map, char Key)> pictures)
        {
            if (images.ResolveTexture(texture) is not { } source)
                return null;

            int width, height;
            byte[] original;
            string? fourcc = null;
            if (source.IsDds)
            {
                // The game reads nothing else from a .dds, so neither do we
                if ((fourcc = Dds.FourCC(source.Data)) == null)
                    return null;
                height = BitConverter.ToInt32(source.Data, 12);
                width = BitConverter.ToInt32(source.Data, 16);
                original = Dds.DecodePixels(source.Data, fourcc, width, height, Dds.HeaderSize);
            }
            else
            {
                if (TgaImage.Decode(source.Data, keepAlpha: true) is not { } tga)
                    return null;
                width = tga.PixelWidth;
                height = tga.PixelHeight;
                original = new byte[width * height * 4];
                tga.CopyPixels(original, width * 4, 0);
            }

            var badges = new List<(Int32Rect Rect, bool Rotated, char Key)>();
            foreach (var (map, key) in pictures)
            {
                if (GameImages.PixelRect(map, width, height) is { } rect && !badges.Any(b => b.Rect == rect))
                    badges.Add((rect, map.Rotated, key));
            }
            if (badges.Count == 0)
                return null;

            byte[] pixels = (byte[])original.Clone();
            Composite(pixels, RenderOverlay(width, height, badges));

            string folder = $@"Data\{images.Language}\Art\Textures\";
            return fourcc != null
                ? new LetteredTexture(texture, folder + texture[..^3] + "dds", EncodeDds(source.Data, fourcc, width, height, original, pixels), badges.Count)
                : new LetteredTexture(texture, folder + texture, WriteTga(width, height, pixels), badges.Count);
        }

        // Straight-alpha pixels with a premultiplied overlay drawn over them
        private static void Composite(byte[] pixels, byte[] overlay)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int ao = overlay[i + 3];
                if (ao == 0)
                    continue;
                double a = pixels[i + 3] / 255.0, top = ao / 255.0;
                double outA = top + a * (1 - top);
                for (int c = 0; c < 3; c++)
                {
                    double value = (overlay[i + c] / 255.0 + pixels[i + c] / 255.0 * a * (1 - top)) / outA;
                    pixels[i + c] = (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
                }
                pixels[i + 3] = (byte)Math.Round(outA * 255);
            }
        }

        // The original first level with only the blocks that changed encoded again
        private static byte[] EncodeDds(byte[] source, string fourcc, int width, int height, byte[] before, byte[] after)
        {
            int blockSize = Dds.BlockSize(fourcc), blocksWide = (width + 3) / 4, blocksHigh = (height + 3) / 4;
            byte[] level = new byte[blocksWide * blocksHigh * blockSize];
            Array.Copy(source, Dds.HeaderSize, level, 0, Math.Min(level.Length, source.Length - Dds.HeaderSize));

            byte[] block = new byte[64];
            for (int by = 0; by < blocksHigh; by++)
            for (int bx = 0; bx < blocksWide; bx++)
            {
                bool changed = false;
                for (int i = 0; i < 16; i++)
                {
                    int x = Math.Min(bx * 4 + i % 4, width - 1), y = Math.Min(by * 4 + i / 4, height - 1);
                    int o = (y * width + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        block[i * 4 + c] = after[o + c];
                        changed |= after[o + c] != before[o + c];
                    }
                }
                if (changed)
                    Dxt.EncodeBlock(block, level.AsSpan((by * blocksWide + bx) * blockSize, blockSize), fourcc);
            }
            return Dxt.WriteDds(width, height, fourcc, level, Marker);
        }

        // 32-bit TGA laid out like the game's own (bottom-up, 2.0 footer), with the marker as the image ID
        private static byte[] WriteTga(int width, int height, byte[] pixels)
        {
            byte[] id = Encoding.ASCII.GetBytes(Marker);
            byte[] file = new byte[18 + id.Length + pixels.Length + 26];
            file[0] = (byte)id.Length;
            file[2] = 2;
            file[12] = (byte)width; file[13] = (byte)(width >> 8);
            file[14] = (byte)height; file[15] = (byte)(height >> 8);
            file[16] = 32;
            file[17] = 8;
            id.CopyTo(file, 18);
            int start = 18 + id.Length, row = width * 4;
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, (height - 1 - y) * row, file, start + y * row, row);
            Encoding.ASCII.GetBytes("TRUEVISION-XFILE.").CopyTo(file, file.Length - 18);
            return file;
        }

        // ── Drawing ──

        private static readonly Brush BoxBrush = Frozen(new SolidColorBrush(Color.FromArgb(225, 10, 12, 16)));
        private static readonly Pen BoxPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255))), 1));
        private static readonly Typeface LetterFace = new(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        // The badges over a transparent texture-sized layer (premultiplied BGRA)
        private static byte[] RenderOverlay(int width, int height, List<(Int32Rect Rect, bool Rotated, char Key)> badges)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                foreach (var (rect, rotated, key) in badges)
                {
                    // A rotated picture is shown turned back: screen x runs down the texture, screen y runs right to left
                    double shownWidth = rotated ? rect.Height : rect.Width, shownHeight = rotated ? rect.Width : rect.Height;
                    dc.PushTransform(rotated
                        ? new MatrixTransform(0, 1, -1, 0, rect.X + rect.Width, rect.Y)
                        : new TranslateTransform(rect.X, rect.Y));
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, shownWidth, shownHeight)));
                    DrawBadge(dc, shownWidth, shownHeight, key);
                    dc.Pop();
                    dc.Pop();
                }
            }
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            byte[] overlay = new byte[width * height * 4];
            target.CopyPixels(overlay, width * 4, 0);
            return overlay;
        }

        private static BitmapSource Render(BitmapSource icon, char key, double scale)
        {
            int w = Math.Max(1, (int)Math.Round(icon.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(icon.PixelHeight * scale));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(icon, new Rect(0, 0, w, h));
                if (key != '\0')
                {
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    DrawBadge(dc, icon.PixelWidth, icon.PixelHeight, key);
                    dc.Pop();
                }
            }
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }

        // Bottom left: a dark rounded box with the letter in white, about two fifths of the picture's height
        private static void DrawBadge(DrawingContext dc, double width, double height, char key)
        {
            double size = Math.Round(Math.Clamp(Math.Min(width, height) * 0.4, 9, 96));
            double inset = Math.Max(1, Math.Round(height * 0.05));
            var text = new FormattedText(key.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LetterFace, 100, Brushes.White, 1.0);
            var glyph = text.BuildGeometry(new Point(0, 0));
            var bounds = glyph.Bounds;
            if (bounds.IsEmpty || bounds.Height <= 0)
                return;

            // Letters and digits stand about 60% of the box high
            double letterScale = size * 0.6 / bounds.Height;
            double boxWidth = Math.Max(size, Math.Round(bounds.Width * letterScale + size * 0.4));
            var box = new Rect(inset, height - inset - size, boxWidth, size);
            double radius = Math.Max(1.5, size * 0.2);
            dc.DrawRoundedRectangle(BoxBrush, null, box, radius, radius);
            dc.DrawRoundedRectangle(null, BoxPen, new Rect(box.X + 0.5, box.Y + 0.5, box.Width - 1, box.Height - 1), radius - 0.5, radius - 0.5);

            var place = new TransformGroup();
            place.Children.Add(new TranslateTransform(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2));
            place.Children.Add(new ScaleTransform(letterScale, letterScale));
            place.Children.Add(new TranslateTransform(box.X + box.Width / 2, box.Y + box.Height / 2));
            dc.PushTransform(place);
            dc.DrawGeometry(Brushes.White, null, glyph);
            dc.Pop();
        }

        // ── Our files ──

        public static bool IsGenerated(byte[] data)
        {
            if (data.Length >= Dds.HeaderSize && Encoding.ASCII.GetString(data, 0, 4) == "DDS ")
                return Encoding.ASCII.GetString(data, 60, Marker.Length) == Marker;
            return data.Length >= 18 + Marker.Length && data[0] == Marker.Length && Encoding.ASCII.GetString(data, 18, Marker.Length) == Marker;
        }

        private static bool IsGeneratedFile(string path)
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

        private static List<string> OwnTargets() =>
            BackupService.Load().Where(e => e.Title == Title).Select(e => e.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Takes our earlier file away before a new one is written: one we created is deleted (it can be built again), one
        // that replaced a file puts that file back. False when the file there is not ours any more, which stays as it is.
        private static bool Clear(string target)
        {
            var entries = BackupService.Load().Where(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.When).ToList();
            if (File.Exists(target))
            {
                if (!IsGeneratedFile(target))
                    return !entries.Any(e => e.Title == Title);
                if (entries.Count == 0 || entries[0].BackupFile == null)
                    File.Delete(target);
            }
            if (entries.Count > 0)
                BackupService.RestoreOriginal(target);
            return true;
        }

        // Files with our marker in the game's texture folders that no backup entry covers
        private static IEnumerable<string> Strays()
        {
            string data = Path.Combine(GamePaths.Game, "Data");
            if (!Directory.Exists(data))
                yield break;
            foreach (string language in Directory.EnumerateDirectories(data))
            {
                string folder = Path.Combine(language, "Art", "Textures");
                if (!Directory.Exists(folder))
                    continue;
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if ((file.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) && IsGeneratedFile(file))
                        yield return file;
                }
            }
        }

        // WPF drawing needs a single-threaded apartment; callers on a worker thread get one of their own
        private static T OnSta<T>(Func<T> work)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return work();
            T result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception e) { error = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null)
                ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
    }
}
