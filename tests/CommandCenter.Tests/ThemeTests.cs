using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace CommandCenter.Tests
{
    // The design tokens (seam 1, plan 3-c): every text and background pair meets WCAG's contrast, controls and the
    // focus ring reach 3:1, and no colour is written outside Theme except in pages that have not moved yet.
    public sealed class ThemeTests
    {
        private static readonly Lazy<ResourceDictionary> Tokens = new(LoadTokens);

        // The compiled dictionary as the program ships it
        private static ResourceDictionary LoadTokens() => OnSta(() =>
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            return (ResourceDictionary)Application.LoadComponent(new Uri("/CommandCenter;component/Theme/Tokens.xaml", UriKind.Relative));
        });

        // Runs on a single-threaded apartment, as WPF wants
        private static T OnSta<T>(Func<T> work)
        {
            T result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return error == null ? result : throw new InvalidOperationException("The theme could not be read.", error);
        }

        private static Color Token(string key) =>
            Tokens.Value[key] is Color color ? color : throw new KeyNotFoundException($"No colour token {key}.");

        // WCAG 2.2 contrast ratio
        private static double Contrast(Color a, Color b)
        {
            static double Channel(byte value)
            {
                double c = value / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        public static TheoryData<string, string> TextPairs()
        {
            var pairs = new TheoryData<string, string>();
            foreach (string text in new[] { "Color.Text.Primary", "Color.Text.Secondary", "Color.Text.Muted", "Color.Accent", "Color.Success", "Color.Warning", "Color.Problem" })
            {
                pairs.Add(text, "Color.Bg.Window");
                pairs.Add(text, "Color.Bg.Panel");
                // Muted text is for labels on the window and panels only; on raised surfaces it would fall under 4.5:1
                if (text != "Color.Text.Muted")
                    pairs.Add(text, "Color.Bg.Raised");
            }
            pairs.Add("Color.OnAccent", "Color.Accent");
            pairs.Add("Color.OnAccent", "Color.Accent.Pressed");
            return pairs;
        }

        [Theory]
        [MemberData(nameof(TextPairs))]
        public void Text_reaches_4_5_to_1_on_every_background_it_sits_on(string text, string background)
        {
            double ratio = Contrast(Token(text), Token(background));
            Assert.True(ratio >= 4.5, $"{text} on {background}: {ratio:0.00}:1");
        }

        [Theory]
        [InlineData("Color.Border", "Color.Bg.Window")]
        [InlineData("Color.Border", "Color.Bg.Panel")]
        [InlineData("Color.Border", "Color.Bg.Raised")]
        [InlineData("Color.Focus", "Color.Bg.Window")]
        [InlineData("Color.Focus", "Color.Bg.Panel")]
        [InlineData("Color.Focus", "Color.Bg.Raised")]
        public void Control_borders_and_the_focus_ring_reach_3_to_1(string line, string background)
        {
            double ratio = Contrast(Token(line), Token(background));
            Assert.True(ratio >= 3, $"{line} on {background}: {ratio:0.00}:1");
        }

        [Fact]
        public void Every_colour_has_a_brush_of_the_same_name()
        {
            foreach (var key in Tokens.Value.Keys.OfType<string>().Where(k => k.StartsWith("Color.", StringComparison.Ordinal)))
            {
                var brush = Assert.IsType<SolidColorBrush>(Tokens.Value[key["Color.".Length..]]);
                Assert.Equal(Token(key), brush.Color);
                Assert.True(brush.IsFrozen, $"{key} is not frozen");
            }
        }

        private static IEnumerable<(string Key, double Value)> Numbers(string prefix) =>
            Tokens.Value.Keys.OfType<string>().Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => (k, (double)Tokens.Value[k]));

        [Fact]
        public void Text_sizes_step_by_1_125_to_1_2_and_none_is_under_12_px()
        {
            // PLAY is the one heroic size outside the scale (plan 3-c)
            double[] sizes = Numbers("Size.").Where(s => s.Key != "Size.Play").Select(s => s.Value).Order().ToArray();
            Assert.True(sizes.Length >= 3);
            Assert.True(sizes[0] >= 12, $"The smallest size is {sizes[0]} px.");
            for (int i = 1; i < sizes.Length; i++)
            {
                double step = sizes[i] / sizes[i - 1];
                Assert.True(step is >= 1.125 and <= 1.2005, $"{sizes[i - 1]} to {sizes[i]} px is a step of {step:0.000}.");
            }
        }

        [Fact]
        public void Spacing_sits_on_a_4_px_grid()
        {
            var spaces = Numbers("Space.").ToArray();
            Assert.NotEmpty(spaces);
            Assert.All(spaces, s => Assert.True(s.Value > 0 && s.Value % 4 == 0, $"{s.Key} is {s.Value} px."));
        }

        // ── Fonts ──

        // Each font token reaches the file inside the program in every weight the design uses, on any PC, instead of
        // falling back to a font Windows has. A font's width comes from its .Stretch token when it has one.
        [Theory]
        [InlineData("Font.Text", 400, "Barlow-Regular.ttf")]
        [InlineData("Font.Text", 500, "Barlow-Medium.ttf")]
        [InlineData("Font.Text", 600, "Barlow-SemiBold.ttf")]
        [InlineData("Font.Heading", 600, "BarlowCondensed-SemiBold.ttf")]
        [InlineData("Font.Heading", 700, "BarlowCondensed-Bold.ttf")]
        [InlineData("Font.Data", 400, "JetBrainsMono-Regular.ttf")]
        public void Each_font_resolves_to_its_file_inside_the_program(string key, int weight, string file)
        {
            string? font = OnSta(() =>
            {
                var stretch = Tokens.Value.Contains(key + ".Stretch") ? (FontStretch)Tokens.Value[key + ".Stretch"] : FontStretches.Normal;
                var typeface = new Typeface((FontFamily)Tokens.Value[key], FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), stretch);
                return typeface.TryGetGlyphTypeface(out var glyphs) ? glyphs.FontUri.ToString() : null;
            });
            Assert.NotNull(font);
            Assert.StartsWith("pack://application:,,,/CommandCenter;component/Theme/Fonts/", font, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("/" + file, font, StringComparison.OrdinalIgnoreCase);
        }

        // ── No colour outside Theme ──

        // Pages and controls that still write colours of their own. A file leaves this list when its page moves to the
        // tokens (the ticket or phase is noted), and the test fails when a file here no longer needs to be listed.
        private static readonly string[] NotYetMoved =
        [
            @"Imaging\FactionArt.cs",                     // phase 6 (the dossier)
            @"MainWindow.xaml",                           // ticket 06
            @"MainWindow.xaml.cs",                        // ticket 06
            @"Pages\LauncherPage.xaml",                   // ticket 13
            @"Pages\LauncherPage.xaml.cs",                // ticket 13
            @"Pages\OptionsPage.xaml",                    // tickets 08 and 09
            @"Pages\OptionsPage.xaml.cs",                 // tickets 08 and 09
            @"Pages\Tools\AddonsSection.xaml",            // ticket 08
            @"Pages\Tools\AddonsSection.xaml.cs",         // ticket 08
            @"Pages\Tools\HealthSection.xaml",            // ticket 10
            @"Pages\Tools\HealthSection.xaml.cs",         // ticket 10
            @"Pages\Tools\HotkeyLettersPanel.xaml",       // phase 6
            @"Pages\Tools\HotkeyLettersPanel.xaml.cs",    // phase 6
            @"Pages\Tools\HotkeysSection.xaml",           // phase 6
            @"Pages\Tools\HotkeysSection.xaml.cs",        // phase 6
            @"Pages\Tools\MapsSection.xaml",              // phase 6
            @"Pages\Tools\MapsSection.xaml.cs",           // phase 6
            @"Pages\Tools\PlayerStrip.cs",                // phase 6
            @"Pages\Tools\ReplaysSection.xaml",           // phase 6
            @"Pages\Tools\ReplaysSection.xaml.cs",        // phase 6
            @"Pages\UpdateOverlay.xaml",                  // ticket 06
            @"Pages\Views.cs",                            // ticket 06
        ];

        // Not Command Center's own look: these draw the hotkey letters into the game's textures
        private static readonly string[] NotInterface = [@"Imaging\IconLettersService.cs"];

        private static readonly Regex Colour = new(
            "[\"'>]#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})[\"'<]" +
            @"|Color\.FromA?Rgb\(|\bColors\.(?!Transparent\b)[A-Z]|\bBrushes\.(?!Transparent\b)[A-Z]" +
            "|(?:Foreground|Background|Fill|Stroke|BorderBrush|Color)=\"(?!Transparent\")[A-Z][a-z]+(?:[A-Z][a-z]+)*\"",
            RegexOptions.Compiled);

        private static string AppFolder()
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            {
                if (File.Exists(Path.Combine(folder.FullName, "CommandCenter.slnx")))
                    return Path.Combine(folder.FullName, "src", "CommandCenter.App");
            }
            throw new DirectoryNotFoundException("The repository was not found above the test folder.");
        }

        [Fact]
        public void No_colour_is_written_outside_Theme_except_in_pages_that_have_not_moved_yet()
        {
            string app = AppFolder();
            var offenders = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(app, f))
                .Where(f => !f.StartsWith(@"obj\", StringComparison.OrdinalIgnoreCase) && !f.StartsWith(@"bin\", StringComparison.OrdinalIgnoreCase)
                            && !f.StartsWith(@"Theme\", StringComparison.OrdinalIgnoreCase) && !NotInterface.Contains(f, StringComparer.OrdinalIgnoreCase))
                .Where(f => Colour.IsMatch(File.ReadAllText(Path.Combine(app, f))))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Empty(offenders.Except(NotYetMoved, StringComparer.OrdinalIgnoreCase));
            Assert.Empty(NotYetMoved.Except(offenders, StringComparer.OrdinalIgnoreCase));
        }
    }
}
