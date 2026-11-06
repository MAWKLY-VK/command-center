using System.Text.Json;

namespace CommandCenter.Tests
{
    // Home's art from the player's game (seam 2, plan 3-c): which picture shows, the tint, and the darkened copy behind
    // the other pages. Pictures are hand-made, and the expected pixels are worked out by hand.
    public sealed class HomeArtTests : IDisposable
    {
        private readonly TempGame _game = new();

        // ── Which picture ──

        private static readonly DecodedImage Tiny = new(1, 1, [0, 0, 0, 255]);

        private static DecodedImage? Every(HomeImage _) => Tiny;

        // A game that has only these pictures
        private static Func<HomeImage, DecodedImage?> Only(params HomeImage[] found) => image => found.Contains(image) ? Tiny : null;

        [Theory]
        [InlineData(null, HomeImage.MainMenu)]
        [InlineData(HomeImage.MainMenu, HomeImage.Usa)]
        [InlineData(HomeImage.Usa, HomeImage.China)]
        [InlineData(HomeImage.China, HomeImage.Gla)]
        [InlineData(HomeImage.Gla, HomeImage.MainMenu)]
        public void Each_start_shows_the_next_picture_in_turn(HomeImage? last, HomeImage expected) =>
            Assert.Equal(expected, HomeArt.Pick(HomeImage.Changing, last, Every)?.Image);

        [Fact]
        public void A_picture_the_game_lacks_is_skipped() =>
            Assert.Equal(HomeImage.Gla, HomeArt.Pick(HomeImage.Changing, HomeImage.MainMenu, Only(HomeImage.MainMenu, HomeImage.Gla))?.Image);

        [Fact]
        public void A_pinned_picture_shows_at_every_start()
        {
            Assert.Equal(HomeImage.China, HomeArt.Pick(HomeImage.China, HomeImage.China, Every)?.Image);
            Assert.Equal(HomeImage.China, HomeArt.Pick(HomeImage.China, null, Every)?.Image);
        }

        [Fact]
        public void A_pinned_picture_the_game_lacks_gives_way_to_the_pictures_in_turn() =>
            Assert.Equal(HomeImage.Usa, HomeArt.Pick(HomeImage.Gla, HomeImage.MainMenu, Only(HomeImage.MainMenu, HomeImage.Usa))?.Image);

        [Fact]
        public void The_chosen_picture_comes_with_its_pixels()
        {
            var usa = new DecodedImage(2, 1, [1, 2, 3, 255, 4, 5, 6, 255]);
            Assert.Same(usa, HomeArt.Pick(HomeImage.Usa, null, image => image == HomeImage.Usa ? usa : null)?.Picture);
        }

        [Fact]
        public void Without_any_picture_there_is_none_and_Home_keeps_its_drawn_background() =>
            Assert.Null(HomeArt.Pick(HomeImage.Changing, null, _ => null));

        // ── The tint ──

        private static readonly ArtTint Tint = new(new Rgb(20, 26, 32), new Rgb(230, 214, 190), Keep: 0.15);

        private static DecodedImage Pixel(byte r, byte g, byte b) => new(1, 1, [b, g, r, 255]);

        [Theory]
        // black: lightness 0, the shadow colour at 85%
        [InlineData(0, 0, 0, 17, 22, 27)]
        // white: lightness 1, the highlight at 85% plus 15% of white
        [InlineData(255, 255, 255, 234, 220, 200)]
        // red: lightness 0.2126 between shadow and highlight at 85%, plus 15% of its red
        [InlineData(255, 0, 0, 93, 56, 56)]
        public void The_tint_maps_lightness_from_cool_shadows_to_warm_highlights_and_keeps_a_little_colour(
            byte r, byte g, byte b, byte er, byte eg, byte eb)
        {
            var tinted = HomeArt.Tint(Pixel(r, g, b), Tint);
            Assert.Equal([eb, eg, er, 255], tinted.Pixels);
        }

        [Fact]
        public void A_brightness_under_1_darkens_the_tinted_picture()
        {
            // white: 233.75, 220.15 and 199.75 at full brightness, halved
            var tinted = HomeArt.Tint(Pixel(255, 255, 255), Tint with { Brightness = 0.5 });
            Assert.Equal([100, 110, 117, 255], tinted.Pixels);
        }

        // ── The copy behind the pages ──

        private static DecodedImage Uniform(int width, int height, byte value)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
            return new DecodedImage(width, height, pixels);
        }

        [Fact]
        public void The_backdrop_is_a_quarter_of_the_size_at_the_given_brightness()
        {
            var backdrop = HomeArt.Backdrop(Uniform(64, 48, 200), brightness: 0.5);
            Assert.Equal((16, 12), (backdrop.Width, backdrop.Height));
            Assert.All(backdrop.Pixels.Where((_, i) => i % 4 != 3), value => Assert.Equal(100, value));
            Assert.All(backdrop.Pixels.Where((_, i) => i % 4 == 3), value => Assert.Equal(255, value));
        }

        [Fact]
        public void The_backdrop_spreads_a_bright_spot_over_its_neighbours()
        {
            var image = Uniform(64, 64, 0);
            for (int y = 28; y < 36; y++)
                for (int x = 28; x < 36; x++)
                    for (int c = 0; c < 3; c++)
                        image.Pixels[(y * 64 + x) * 4 + c] = 255;

            var backdrop = HomeArt.Backdrop(image, brightness: 1);
            byte Value(int x, int y) => backdrop.Pixels[(y * backdrop.Width + x) * 4];
            Assert.True(Value(8, 8) < 255, "The spot stays sharp.");
            Assert.True(Value(5, 8) > 0, "The light does not reach the neighbours.");
            Assert.Equal(0, Value(0, 0));
        }

        // ── Reading the game's files ──

        // The game's definitions of Home's pictures, here on textures declared 8 x 8 (the game declares 1024 x 1024 and
        // shows the top left 800 x 600)
        private void Define(params (string Name, string Texture, string Coords)[] pictures) =>
            _game.Write(@"game\Data\INI\MappedImages\HandCreated\HandCreatedMappedImages.INI", string.Concat(pictures.Select(p =>
                $"MappedImage {p.Name}\n  Texture = {p.Texture}\n  TextureWidth = 8\n  TextureHeight = 8\n  Coords = {p.Coords}\n  Status = NONE\nEnd\n\n")));

        private GameImages OpenGame() => new(new GameFiles(_game.Game, communityPatch: false), "English");

        // 8 x 8, grey 10 in the top left 4 x 2 and grey 200 elsewhere
        private static byte[] Texture()
        {
            var body = new byte[8 * 8 * 3];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    body.AsSpan((y * 8 + x) * 3, 3).Fill(x < 4 && y < 2 ? (byte)10 : (byte)200);
            return Images.Tga(8, 8, 24, topDown: true, rle: false, body);
        }

        [Fact]
        public void The_main_menu_picture_is_the_part_of_its_texture_the_game_shows()
        {
            Define(("MainMenuBackdrop", "MainMenuBackdropuserinterface.tga", "Left:0 Top:0 Right:4 Bottom:2"));
            BigArchives.Write(Path.Combine(_game.Game, @"ZH_Generals\Textures.big"), (@"Art\Textures\mainmenubackdropuserinterface.tga", Texture()));

            var picture = HomeArt.Load(OpenGame(), HomeImage.MainMenu)!;
            Assert.Equal((4, 2), (picture.Width, picture.Height));
            Assert.All(picture.Pixels.Where((_, i) => i % 4 != 3), value => Assert.Equal(10, value));
        }

        [Fact]
        public void A_faction_loading_screen_is_read_from_its_dds()
        {
            Define(("MissionLoad_USA", "MissionLoad_USA.tga", "Left:0 Top:0 Right:8 Bottom:8"),
                   ("MissionLoad_China", "MissionLoad_China.tga", "Left:0 Top:0 Right:8 Bottom:8"));
            // White in every 4 x 4 block
            byte[] white = Images.Dxt1Block(0xFFFF, 0xFFFF, 0);
            BigArchives.Write(Path.Combine(_game.Game, "TexturesZH.big"),
                (@"Art\Textures\missionload_usa.dds", Images.Dds("DXT1", 8, 8, [.. white, .. white, .. white, .. white])));
            var images = OpenGame();

            var usa = HomeArt.Load(images, HomeImage.Usa)!;
            Assert.Equal((8, 8), (usa.Width, usa.Height));
            Assert.Equal([255, 255, 255, 255], usa.Pixels[..4]);
            Assert.Null(HomeArt.Load(images, HomeImage.China));
        }

        [Fact]
        public void A_picture_the_game_does_not_define_is_not_shown()
        {
            BigArchives.Write(Path.Combine(_game.Game, @"ZH_Generals\Textures.big"), (@"Art\Textures\mainmenubackdropuserinterface.tga", Texture()));
            Assert.Null(HomeArt.Load(OpenGame(), HomeImage.MainMenu));
        }

        [Fact]
        public void A_damaged_picture_is_not_shown()
        {
            Define(("MainMenuBackdrop", "MainMenuBackdropuserinterface.tga", "Left:0 Top:0 Right:4 Bottom:2"));
            BigArchives.Write(Path.Combine(_game.Game, @"ZH_Generals\Textures.big"),
                (@"Art\Textures\mainmenubackdropuserinterface.tga", Images.Tga(4000, 4000, 32, topDown: true, rle: false, new byte[64])));
            Assert.Null(HomeArt.Load(OpenGame(), HomeImage.MainMenu));
        }

        // ── The setting ──

        [Fact]
        public void Settings_from_0_4_0_change_the_picture_at_each_start()
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("""{"GameFolder":null,"IconLetters":true}""")!;
            Assert.Equal(HomeImage.Changing, settings.HomeImage);
            Assert.Null(settings.LastHomeImage);
        }

        [Fact]
        public void The_setting_and_the_last_picture_are_kept_by_name()
        {
            string json = JsonSerializer.Serialize(new AppSettings { HomeImage = HomeImage.Gla, LastHomeImage = HomeImage.Usa });
            Assert.Contains("\"Gla\"", json);
            var back = JsonSerializer.Deserialize<AppSettings>(json)!;
            Assert.Equal((HomeImage.Gla, (HomeImage?)HomeImage.Usa), (back.HomeImage, back.LastHomeImage));
        }

        [Fact]
        public void A_picture_name_this_version_does_not_know_reads_as_changing_and_keeps_the_other_settings()
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("""{"GameFolder":"D:\\Game","HomeImageName":"Moon","LastHomeImageName":"9"}""")!;
            Assert.Equal(HomeImage.Changing, settings.HomeImage);
            Assert.Null(settings.LastHomeImage);
            Assert.Equal(@"D:\Game", settings.GameFolder);
        }

        public void Dispose() => _game.Dispose();
    }
}
