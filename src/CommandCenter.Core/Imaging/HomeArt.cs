namespace CommandCenter.Core.Imaging
{
    // The pictures Home can show, all from the player's own game: the main menu's backdrop and the three faction loading
    // screens. Changing shows the next one at each start; any other value pins that picture.
    public enum HomeImage { Changing, MainMenu, Usa, China, Gla }

    public readonly record struct Rgb(byte R, byte G, byte B);

    // The near-monochrome tint of plan 3-c: lightness runs from a cool shadow colour to a warm highlight colour, and Keep
    // (0 to 1) is how much of each pixel's own colour stays
    public sealed record ArtTint(Rgb Shadow, Rgb Highlight, double Keep);

    // The picture Home shows at this start, with its pixels
    public sealed record HomePicture(HomeImage Image, DecodedImage Picture);

    // Home's art (phase 5 spec): which picture shows, read the way the game reads it, tinted once, and the small blurred,
    // darkened copy that the other pages sit on. Nothing here blurs or tints live.
    public static class HomeArt
    {
        private static readonly HomeImage[] InTurn = [HomeImage.MainMenu, HomeImage.Usa, HomeImage.China, HomeImage.Gla];

        // Each picture's MappedImage (HandCreatedMappedImages.ini): an 800 x 600 picture at the top left of its texture
        private static string MappedName(HomeImage image) => image switch
        {
            HomeImage.MainMenu => "MainMenuBackdrop",
            HomeImage.Usa => "MissionLoad_USA",
            HomeImage.China => "MissionLoad_China",
            HomeImage.Gla => "MissionLoad_GLA",
            _ => "",
        };

        // The pinned picture, or the next in turn after the last one shown. A picture that does not load gives way to the
        // next in turn. Null when none loads, and Home keeps its drawn background.
        public static HomePicture? Pick(HomeImage setting, HomeImage? lastShown, Func<HomeImage, DecodedImage?> load)
        {
            int start = lastShown is { } last ? Array.IndexOf(InTurn, last) + 1 : 0;
            var candidates = Enumerable.Range(0, InTurn.Length).Select(k => InTurn[(start + k) % InTurn.Length]);
            if (InTurn.Contains(setting))
                candidates = candidates.Where(image => image != setting).Prepend(setting);
            foreach (var image in candidates)
            {
                if (load(image) is { } picture)
                    return new HomePicture(image, picture);
            }
            return null;
        }

        // The picture as the game shows it: its MappedImage's part of the texture, which GameImages finds the game's way
        // (the language folder first). Null when the game lacks either, or the texture is damaged (written to the error
        // log by the decoder).
        public static DecodedImage? Load(GameImages images, HomeImage image)
        {
            if (images.Find(MappedName(image)) is not { } map || images.TexturePixels(map.Texture) is not { } texture
                || GameImages.PixelRect(map, texture.Width, texture.Height) is not { } rect)
                return null;

            var pixels = new byte[rect.Width * rect.Height * 4];
            for (int y = 0; y < rect.Height; y++)
                Buffer.BlockCopy(texture.Pixels, ((rect.Y + y) * texture.Width + rect.X) * 4, pixels, y * rect.Width * 4, rect.Width * 4);
            return new DecodedImage(rect.Width, rect.Height, pixels);
        }

        // Each pixel's lightness (Rec. 709 luma) picks a colour between the tint's shadow and highlight, and Keep of the
        // pixel's own colour is mixed back in. Alpha stays as it was.
        public static DecodedImage Tint(DecodedImage image, ArtTint tint)
        {
            double keep = Math.Clamp(tint.Keep, 0, 1), mono = 1 - keep;
            byte[] source = image.Pixels, pixels = new byte[source.Length];
            for (int i = 0; i + 3 < source.Length; i += 4)
            {
                byte b = source[i], g = source[i + 1], r = source[i + 2];
                double lightness = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
                pixels[i] = Mix(tint.Shadow.B, tint.Highlight.B, b);
                pixels[i + 1] = Mix(tint.Shadow.G, tint.Highlight.G, g);
                pixels[i + 2] = Mix(tint.Shadow.R, tint.Highlight.R, r);
                pixels[i + 3] = source[i + 3];

                byte Mix(byte shadow, byte highlight, byte own) =>
                    (byte)Math.Clamp(Math.Round(mono * (shadow + (highlight - shadow) * lightness) + keep * own), 0, 255);
            }
            return new DecodedImage(image.Width, image.Height, pixels);
        }

        // The copy behind the other pages: a quarter of the width and height, blurred, its colours multiplied by
        // brightness (0 to 1), and opaque. Made once when the picture loads (plan 3-c: no live blur).
        public static DecodedImage Backdrop(DecodedImage image, double brightness)
        {
            int width = Math.Max(1, image.Width / 4), height = Math.Max(1, image.Height / 4);
            int blockWidth = Math.Min(4, image.Width), blockHeight = Math.Min(4, image.Height);

            // Each pixel of the small copy is the average of the block it covers
            var colour = new float[width * height * 3];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        float sum = 0;
                        for (int dy = 0; dy < blockHeight; dy++)
                            for (int dx = 0; dx < blockWidth; dx++)
                                sum += image.Pixels[((y * blockHeight + dy) * image.Width + x * blockWidth + dx) * 4 + c];
                        colour[(y * width + x) * 3 + c] = sum / (blockWidth * blockHeight);
                    }

            // Three passes of a box blur each way come close to a Gaussian blur
            int radius = Math.Max(1, width / 40);
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(colour, width, height, radius, alongRows: true);
                BoxBlur(colour, width, height, radius, alongRows: false);
            }

            double scale = Math.Clamp(brightness, 0, 1);
            var pixels = new byte[width * height * 4];
            for (int p = 0; p < width * height; p++)
            {
                for (int c = 0; c < 3; c++)
                    pixels[p * 4 + c] = (byte)Math.Clamp(Math.Round(colour[p * 3 + c] * scale), 0, 255);
                pixels[p * 4 + 3] = 255;
            }
            return new DecodedImage(width, height, pixels);
        }

        // Each value becomes the average of those within radius along its row (or column); near the edges only the values
        // that exist count, so an even colour stays even
        private static void BoxBlur(float[] colour, int width, int height, int radius, bool alongRows)
        {
            int lines = alongRows ? height : width, length = alongRows ? width : height;
            var line = new float[length * 3];
            for (int l = 0; l < lines; l++)
            {
                int Index(int k) => (alongRows ? l * width + k : k * width + l) * 3;
                for (int k = 0; k < length; k++)
                    for (int c = 0; c < 3; c++)
                        line[k * 3 + c] = colour[Index(k) + c];
                for (int k = 0; k < length; k++)
                {
                    int from = Math.Max(0, k - radius), to = Math.Min(length - 1, k + radius);
                    for (int c = 0; c < 3; c++)
                    {
                        float sum = 0;
                        for (int j = from; j <= to; j++)
                            sum += line[j * 3 + c];
                        colour[Index(k) + c] = sum / (to - from + 1);
                    }
                }
            }
        }
    }
}
