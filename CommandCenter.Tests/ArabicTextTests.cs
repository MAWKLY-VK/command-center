using System.Text;

namespace CommandCenter.Tests
{
    public class ArabicShapingTests
    {
        [Theory]
        // seen at the start, lam-alef joined to it, meem alone
        [InlineData("سلام", "\uFEB3\uFEFC\uFEE1")]
        // kaf start, teh middle, alef end, beh alone (alef does not join the next letter)
        [InlineData("كتاب", "\uFEDB\uFE98\uFE8E\uFE8F")]
        // lam-alef alone
        [InlineData("لا", "\uFEFB")]
        // hamza has a single shape and breaks the word
        [InlineData("ماء", "\uFEE3\uFE8E\uFE80")]
        // teh marbuta at the end
        [InlineData("قوة", "\uFED7\uFEEE\uFE93")]
        public void Letters_get_their_joined_shapes(string word, string shaped) => Assert.Equal(shaped, ArabicShaping.Shape(word));

        [Fact]
        public void A_line_is_stored_reversed() =>
            Assert.Equal("\uFE8E\uFE92\uFEA3\uFEAE\uFEE3", ArabicShaping.ToVisualLine("مرحبا"));

        [Fact]
        public void Words_keep_their_order_from_the_right()
        {
            // "كتاب سلام" is drawn as: سلام (left), space, كتاب (right)
            Assert.Equal(ArabicShaping.ToVisualLine("سلام") + " " + ArabicShaping.ToVisualLine("كتاب"), ArabicShaping.ToVisualLine("كتاب سلام"));
        }

        [Fact]
        public void Numbers_and_tokens_stay_left_to_right()
        {
            string visual = ArabicShaping.ToVisualLine("التكلفة: %d");
            Assert.StartsWith("%d :", visual);
            Assert.Contains("1.04", ArabicShaping.ToVisualLine("الإصدار 1.04"));
        }

        // The opening bracket ends up on the right of the word, where it has to curve towards it: stored left to right
        // the pair reads "(...)" again, because each bracket was reversed and mirrored
        [Fact]
        public void Brackets_inside_arabic_are_mirrored() =>
            Assert.Equal("(" + ArabicShaping.ToVisualLine("سلام") + ")", ArabicShaping.ToVisualLine("(سلام)"));

        [Fact]
        public void Persian_letters_digits_and_harakat_are_made_safe()
        {
            Assert.Equal("علي", ArabicShaping.Normalize("علی"));
            Assert.Equal("كتاب", ArabicShaping.Normalize("کتاب"));
            Assert.Equal("123", ArabicShaping.Normalize("١٢٣"));
            Assert.Equal("مالا", ArabicShaping.Normalize("مالاً"));
        }

        [Theory]
        [InlineData("Cost: %d", new[] { "%d" })]
        [InlineData("%ls does not have the map %ls", new[] { "%ls", "%ls" })]
        [InlineData("%d:%02d:%02d remaining", new[] { "%d", "%02d", "%02d" })]
        [InlineData("25% extra damage, 100%% sure", new string[0])]
        public void Printf_tokens_are_found(string text, string[] tokens) => Assert.Equal(tokens, ArabicShaping.Tokens(text));
    }

    public class ArabicGameTextTests
    {
        [Fact]
        public void A_button_gets_its_hotkey_after_the_name()
        {
            string visual = ArabicGameText.Visual("Hum&vee", "همفي", 'V')!;
            Assert.StartsWith("(&V) ", visual);
            Assert.Equal('V', CsfFile.HotkeyOf(visual));
        }

        [Fact]
        public void Values_are_filled_in_their_english_places()
        {
            // The game fills the first %ls (the player) into the leftmost token, so in Arabic the map has to come first
            string visual = ArabicGameText.Visual("%ls does not have the map %ls", "الخريطة {2} غير موجودة عند {1}", '\0')!;
            Assert.NotNull(visual);
            Assert.Equal(new[] { "%ls", "%ls" }, ArabicShaping.Tokens(visual));
            Assert.Null(ArabicGameText.Visual("%ls does not have the map %ls", "لا يملك {1} الخريطة {2}", '\0'));
        }

        [Theory]
        [InlineData("Cost: %d", "التكلفة: %d")]
        [InlineData("Cost: %d", "التكلفة")]
        [InlineData("Cost: %d", "التكلفة: {2}")]
        public void A_translation_with_wrong_values_stays_english(string english, string arabic) =>
            Assert.Null(ArabicGameText.Visual(english, arabic, '\0'));

        [Fact]
        public void Long_text_is_broken_into_lines_no_wider_than_the_english()
        {
            const string english = "Strong vs. vehicles, buildings\nWeak vs. aircraft, missile armed infantry";
            string arabic = "قوية جدا ضد العربات المدرعة والمباني الدفاعية وكل أنواع الدبابات الثقيلة\nضعيفة ضد الطائرات";
            string visual = ArabicGameText.Visual(english, arabic, '\0')!;
            var lines = visual.Split('\n');
            Assert.True(lines.Length >= 3);
            double limit = ArabicGameText.WrapWidth(english);
            Assert.All(lines, line => Assert.True(ArabicShaping.Width(line) <= limit + 0.01, $"{ArabicShaping.Width(line)} > {limit}"));
        }

        [Fact]
        public void Names_stay_on_one_line() =>
            Assert.DoesNotContain('\n', ArabicGameText.Visual("&Crusader", "كروسيدر طويل الاسم جدا جدا", 'C')!);
    }

    // The table the game reads, built from a small game table the way Command Center builds the real one
    public class ArabicTableTests
    {
        private static byte[] GameTable(params (string Label, string Text)[] labels)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write(Encoding.ASCII.GetBytes(" FSC"));
            w.Write(3);
            w.Write(labels.Length);
            w.Write(labels.Length);
            w.Write(0);
            w.Write(0);
            foreach (var (label, text) in labels)
            {
                w.Write(Encoding.ASCII.GetBytes(" LBL"));
                w.Write(1);
                w.Write(label.Length);
                w.Write(Encoding.ASCII.GetBytes(label));
                w.Write(Encoding.ASCII.GetBytes(" RTS"));
                w.Write(text.Length);
                w.Write(Encoding.Unicode.GetBytes(text).Select(b => (byte)~b).ToArray());
            }
            w.Flush();
            return stream.ToArray();
        }

        private static readonly byte[] Original = GameTable(
            ("CONTROLBAR:ConstructAmericaVehicleHumvee", "Hum&vee"),
            ("OBJECT:Humvee", "Humvee"),
            ("GUI:Ok", "OK"),
            ("GUI:NotTranslated", "Stays English"));

        [Fact]
        public void Turning_on_translates_and_keeps_the_game_keys()
        {
            var built = ArabicGameText.Build(Original, CsfFile.Load(Original), out int translated);
            Assert.Equal(3, translated);
            Assert.True(ArabicGameText.IsIn(built));
            Assert.True(ArabicShaping.HasArabic(built.Get("OBJECT:Humvee")));
            Assert.Equal('V', CsfFile.HotkeyOf(built.Get("CONTROLBAR:ConstructAmericaVehicleHumvee")));
            Assert.Equal("Stays English", built.Get("GUI:NotTranslated"));
        }

        [Fact]
        public void A_saved_hotkey_stays_when_turning_on_and_off()
        {
            var saved = CsfFile.Load(Original);
            saved.Set("CONTROLBAR:ConstructAmericaVehicleHumvee", "&Humvee");
            var arabic = ArabicGameText.Build(Original, saved, out _);
            Assert.Equal('H', CsfFile.HotkeyOf(arabic.Get("CONTROLBAR:ConstructAmericaVehicleHumvee")));

            var english = ArabicGameText.Strip(Original, CsfFile.Load(arabic.ToBytes()));
            Assert.Equal("&Humvee", english.Get("CONTROLBAR:ConstructAmericaVehicleHumvee"));
            Assert.Equal("Humvee", english.Get("OBJECT:Humvee"));
            Assert.False(ArabicGameText.IsIn(english));
        }

        [Fact]
        public void Turning_off_gives_back_the_game_table_byte_for_byte()
        {
            var arabic = ArabicGameText.Build(Original, CsfFile.Load(Original), out _);
            var english = ArabicGameText.Strip(Original, CsfFile.Load(arabic.ToBytes()));
            Assert.Equal(Original, english.ToBytes());
        }

        [Fact]
        public void Added_labels_update_the_header_counts()
        {
            var table = CsfFile.Load(Original);
            table.Add("GUI:New", "new");
            var again = CsfFile.Load(table.ToBytes());
            Assert.Equal("new", again.Get("GUI:New"));
            Assert.Equal(5, BitConverter.ToInt32(table.ToBytes(), 8));
            Assert.True(again.Remove("GUI:New"));
            Assert.Equal(Original, again.ToBytes());
        }

        [Fact]
        public void A_game_that_already_shows_arabic_is_recognised()
        {
            var labels = Enumerable.Range(0, 25).Select(i => ($"GUI:Ar{i}", "\uFE8E\uFE92\uFEA3")).ToArray();
            Assert.True(ArabicGameText.GameHasArabic(CsfFile.Load(GameTable(labels))));
            Assert.False(ArabicGameText.GameHasArabic(CsfFile.Load(Original)));
        }
    }

    // The translation file itself
    public class ArabicTranslationFileTests
    {
        private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{(\d+)\}");

        [Fact]
        public void Every_text_follows_the_rules()
        {
            var wrong = new List<string>();
            foreach (var (label, arabic) in ArabicGameText.Translation.Text)
            {
                if (!label.Contains(':'))
                    wrong.Add($"{label}: not a game label");
                if (ArabicShaping.Normalize(arabic) != arabic)
                    wrong.Add($"{label}: harakat, tatweel, Persian letters or Arabic-Indic digits");
                if (arabic.Contains('&'))
                    wrong.Add($"{label}: '&' (hotkeys are added by Command Center)");
                if (ArabicShaping.Tokens(arabic).Count > 0)
                    wrong.Add($"{label}: write values as {{1}}, {{2}}");
                if (!ArabicShaping.HasArabic(arabic))
                    wrong.Add($"{label}: no Arabic");
                var numbers = Placeholder.Matches(arabic).Select(m => int.Parse(m.Groups[1].Value)).OrderBy(n => n).ToList();
                if (!numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)))
                    wrong.Add($"{label}: values must be {{1}}..{{n}}, each once");
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        // On a PC with Zero Hour installed, every label has to exist in the game's table (or be listed as added) and
        // come out in Arabic with its values in place
        [Fact]
        public void Every_text_fits_the_installed_game()
        {
            string game = @"C:\Program Files (x86)\Steam\steamapps\common\Command & Conquer Generals - Zero Hour";
            if (!File.Exists(Path.Combine(game, "EnglishZH.big")))
                return;
            var files = new GameFiles(game, communityPatch: false);
            var table = CsfFile.Load(files.ReadFromArchive(ArabicGameText.CsfPath("English"))!);
            var wrong = new List<string>();
            foreach (var (label, arabic) in ArabicGameText.Translation.Text)
            {
                string? english = table.Get(label);
                if (english == null && !ArabicGameText.Translation.Added.Contains(label))
                    wrong.Add($"{label}: not in the game");
                else if (ArabicGameText.Visual(english ?? "", arabic, CsfFile.HotkeyOf(english)) == null)
                    wrong.Add($"{label}: values out of place or missing");
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }
    }
}
