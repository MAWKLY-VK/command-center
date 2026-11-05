using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Imaging;

namespace CommandCenter.Tests
{
    // Hand-made images whose decoded pixels are worked out by hand, so the expected values never come from the code
    // under test. These tests pin what the game's pictures look like before and after the decoding moves out of WPF.
    internal static class Images
    {
        public static byte[] Tga(int width, int height, int bitsPerPixel, bool topDown, bool rle, params byte[] body)
        {
            var file = new byte[18 + body.Length];
            file[2] = (byte)(rle ? 10 : 2);
            file[12] = (byte)width;
            file[13] = (byte)(width >> 8);
            file[14] = (byte)height;
            file[15] = (byte)(height >> 8);
            file[16] = (byte)bitsPerPixel;
            file[17] = (byte)((topDown ? 0x20 : 0) | (bitsPerPixel == 32 ? 8 : 0));
            body.CopyTo(file, 18);
            return file;
        }

        public static byte[] Dds(string fourcc, int width, int height, params byte[] blocks)
        {
            var file = new byte[128 + blocks.Length];
            Encoding.ASCII.GetBytes("DDS ").CopyTo(file, 0);
            BitConverter.GetBytes(124).CopyTo(file, 4);
            BitConverter.GetBytes(height).CopyTo(file, 12);
            BitConverter.GetBytes(width).CopyTo(file, 16);
            Encoding.ASCII.GetBytes(fourcc).CopyTo(file, 84);
            blocks.CopyTo(file, 128);
            return file;
        }

        // A DXT1 block: two RGB565 colors, then a 2-bit color index per pixel, row by row
        public static byte[] Dxt1Block(ushort c0, ushort c1, uint indices) =>
            [.. BitConverter.GetBytes(c0), .. BitConverter.GetBytes(c1), .. BitConverter.GetBytes(indices)];

        // Straight BGRA bytes, top row first
        public static byte[]? Pixels(DecodedImage? image) => image?.Pixels;

        public static byte[]? Pixels(BitmapSource? image)
        {
            if (image == null)
                return null;
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            return pixels;
        }
    }

    public class TgaImageTests
    {
        [Fact]
        public void A_bottom_up_24_bit_picture_comes_out_top_row_first_and_opaque()
        {
            // The file holds the bottom row first: (1,2,3) (4,5,6), then the top row: (7,8,9) (10,11,12)
            byte[] file = Images.Tga(2, 2, 24, topDown: false, rle: false, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
            Assert.Equal(new byte[] { 7, 8, 9, 255, 10, 11, 12, 255, 1, 2, 3, 255, 4, 5, 6, 255 }, Images.Pixels(TgaImage.Decode(file)));
        }

        [Fact]
        public void A_run_length_32_bit_picture_keeps_its_alpha_only_when_asked()
        {
            // A run of two pixels (10,20,30 with alpha 40), then one plain pixel (50,60,70 with alpha 80)
            byte[] file = Images.Tga(3, 1, 32, topDown: true, rle: true, 0x81, 10, 20, 30, 40, 0x00, 50, 60, 70, 80);
            Assert.Equal(new byte[] { 10, 20, 30, 40, 10, 20, 30, 40, 50, 60, 70, 80 }, Images.Pixels(TgaImage.Decode(file, keepAlpha: true)));
            Assert.Equal(new byte[] { 10, 20, 30, 255, 10, 20, 30, 255, 50, 60, 70, 255 }, Images.Pixels(TgaImage.Decode(file)));
        }

        [Fact]
        public void Pictures_the_game_cannot_use_give_nothing()
        {
            byte[] colorMapped = Images.Tga(1, 1, 24, topDown: true, rle: false, 1, 2, 3);
            colorMapped[1] = 1;
            Assert.Null(TgaImage.Decode(colorMapped));
            Assert.Null(TgaImage.Decode(Images.Tga(1, 1, 16, topDown: true, rle: false, 1, 2)));
            Assert.Null(TgaImage.Decode(Images.Tga(2, 2, 24, topDown: true, rle: false, 1, 2, 3)));
            Assert.Null(TgaImage.Decode(new byte[10]));
        }
    }

    public class DdsTests
    {
        // Every row of the 4 x 4 block uses the color indices 0, 1, 2 and 3 from left to right
        private const uint RowsOf0123 = 0xE4E4E4E4;

        [Fact]
        public void A_dxt1_block_whose_first_color_is_larger_has_four_opaque_colors()
        {
            // 0xF800 is pure red, 0x001F pure blue; the other two lie 1/3 and 2/3 of the way from red to blue
            byte[] file = Images.Dds("DXT1", 4, 4, Images.Dxt1Block(0xF800, 0x001F, RowsOf0123));
            byte[] row = [0, 0, 255, 255, 255, 0, 0, 255, 85, 0, 170, 255, 170, 0, 85, 255];
            Assert.Equal(Repeat(row, 4), Images.Pixels(Dds.Decode(file)));
        }

        [Fact]
        public void A_dxt1_block_whose_first_color_is_not_larger_has_a_transparent_fourth_color()
        {
            // Blue first, then red: the third color is their average and the fourth is fully transparent
            byte[] file = Images.Dds("DXT1", 4, 4, Images.Dxt1Block(0x001F, 0xF800, RowsOf0123));
            byte[] row = [255, 0, 0, 255, 0, 0, 255, 255, 127, 0, 127, 255, 0, 0, 0, 0];
            Assert.Equal(Repeat(row, 4), Images.Pixels(Dds.Decode(file)));
        }

        [Fact]
        public void A_texture_the_game_cannot_read_gives_nothing()
        {
            Assert.Null(Dds.Decode(Images.Dds("DXT2", 4, 4, new byte[16])));
            byte[] notDds = Images.Dds("DXT1", 4, 4, new byte[8]);
            notDds[0] = (byte)'X';
            Assert.Null(Dds.Decode(notDds));
        }

        private static byte[] Repeat(byte[] row, int times) => Enumerable.Repeat(row, times).SelectMany(r => r).ToArray();
    }

    // A game folder with one 4 x 2 atlas holding two pictures, and an empty user data folder
    public sealed class GameImagesTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _previousUserData = GamePaths.UserData;
        private readonly GameImages _images;

        public GameImagesTests()
        {
            string game = Path.Combine(_root, "game");
            GamePaths.UserData = Path.Combine(_root, "user");
            WriteText(game, @"Data\INI\MappedImages\TextureSize_512\Test.ini", """
                MappedImage TestUpright
                  Texture = TestAtlas.tga
                  TextureWidth = 4
                  TextureHeight = 2
                  Coords = Left:1 Top:0 Right:3 Bottom:2
                  Status = NONE
                End

                MappedImage TestRotated
                  Texture = TestAtlas.tga
                  TextureWidth = 4
                  TextureHeight = 2
                  Coords = Left:0 Top:0 Right:2 Bottom:1
                  Status = ROTATED_90_CLOCKWISE
                End
                """);
            // The atlas row by row, all opaque: (1,2,3) (4,5,6) (7,8,9) (10,11,12), then (13,14,15) (16,17,18) (19,20,21) (22,23,24)
            WriteBytes(game, @"Art\Textures\TestAtlas.tga", Images.Tga(4, 2, 32, topDown: true, rle: false,
                1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255,
                13, 14, 15, 255, 16, 17, 18, 255, 19, 20, 21, 255, 22, 23, 24, 255));
            _images = new GameImages(new GameFiles(game, communityPatch: false), "English");
        }

        [Fact]
        public void A_picture_is_found_with_its_texture_and_rectangle()
        {
            var upright = _images.Find("TestUpright");
            Assert.NotNull(upright);
            Assert.Equal("TestAtlas.tga", upright.Texture);
            Assert.Equal((1, 0, 2, 2), (upright.Coords.X, upright.Coords.Y, upright.Coords.Width, upright.Coords.Height));
            Assert.False(upright.Rotated);
            Assert.True(_images.Find("TestRotated")!.Rotated);
            Assert.Null(_images.Find("NotDefined"));
        }

        [Fact]
        public void A_picture_is_cut_out_of_its_atlas()
        {
            var picture = _images.Get("TestUpright");
            Assert.NotNull(picture);
            Assert.Equal((2, 2), (picture.PixelWidth, picture.PixelHeight));
            Assert.Equal(new byte[] { 4, 5, 6, 255, 7, 8, 9, 255, 16, 17, 18, 255, 19, 20, 21, 255 }, Images.Pixels(picture));
        }

        [Fact]
        public void A_picture_stored_turned_is_turned_back_upright()
        {
            // Stored turned 90 degrees clockwise: the 2 x 1 piece (1,2,3) (4,5,6) is shown 1 x 2, with (4,5,6) on top
            var picture = _images.Get("TestRotated");
            Assert.NotNull(picture);
            Assert.Equal((1, 2), (picture.PixelWidth, picture.PixelHeight));
            Assert.Equal(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255 }, Images.Pixels(picture));
        }

        [Fact]
        public void Coordinates_follow_the_real_texture_size()
        {
            var half = GameImages.PixelRect(new MappedImage("Half", "Atlas.tga", 512, 512, new ImageRect(64, 32, 32, 16), false), 256, 256);
            Assert.NotNull(half);
            Assert.Equal((32, 16, 16, 8), (half.Value.X, half.Value.Y, half.Value.Width, half.Value.Height));

            // A piece reaching past the real texture is cut at its edge; nothing is left of one fully outside
            var edge = GameImages.PixelRect(new MappedImage("Edge", "Atlas.tga", 4, 4, new ImageRect(2, 2, 4, 4), false), 4, 4);
            Assert.NotNull(edge);
            Assert.Equal((2, 2, 2, 2), (edge.Value.X, edge.Value.Y, edge.Value.Width, edge.Value.Height));
            Assert.Null(GameImages.PixelRect(new MappedImage("Outside", "Atlas.tga", 4, 4, new ImageRect(8, 8, 2, 2), false), 4, 4));
        }

        public void Dispose()
        {
            GamePaths.UserData = _previousUserData;
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static void WriteText(string root, string relative, string text) =>
            WriteBytes(root, relative, Encoding.Latin1.GetBytes(text));

        private static void WriteBytes(string root, string relative, byte[] content)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }
}
