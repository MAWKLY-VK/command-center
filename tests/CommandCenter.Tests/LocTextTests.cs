namespace CommandCenter.Tests
{
    public class LocTextTests
    {
        private const char Ltr = Loc.LtrMark;

        public LocTextTests() => Loc.Init("en");

        [Fact]
        public void English_text_is_kept_for_the_support_report()
        {
            var text = LocText.F("Missing: {0}", "INIZH.big");
            Assert.Equal("Missing: INIZH.big", text.ToString());
            using (new Arabic())
                Assert.Equal("Missing: INIZH.big", text.ToString());
        }

        [Theory]
        [InlineData(new[] { "a.big" }, "a.big")]
        [InlineData(new[] { "a.big", "b.big" }, "a.big and b.big")]
        [InlineData(new[] { "a.big", "b.big", "c.big" }, "a.big, b.big and c.big")]
        [InlineData(new[] { "a.big", "b.big", "c.big", "d.big", "e.big" }, "a.big, b.big, c.big and 2 more")]
        public void Names_are_listed_in_English(string[] names, string expected) =>
            Assert.Equal(expected, LocText.Names(names).ToString());

        [Fact]
        public void Plural_uses_one_for_a_single_item()
        {
            Assert.Equal("1 archive matches Zero Hour 1.04", LocText.N(1, "{0} archive matches Zero Hour 1.04", "{0} archives match Zero Hour 1.04").ToString());
            Assert.Equal("3 archives match Zero Hour 1.04", LocText.N(3, "{0} archive matches Zero Hour 1.04", "{0} archives match Zero Hour 1.04").ToString());
        }

        [Fact]
        public void Joined_sentences_keep_their_order()
        {
            var text = LocText.Join(". ", new[] { LocText.F("Missing: {0}", "a.big"), LocText.F("Damaged or changed: {0}", "b.big") });
            Assert.Equal("Missing: a.big. Damaged or changed: b.big", text.ToString());
        }

        [Fact]
        public void Arabic_translates_the_sentence_and_keeps_the_file_name_left_to_right()
        {
            using var _ = new Arabic();
            Assert.Equal($"مفقود: {Ltr}INIZH.big{Ltr}", LocText.F("Missing: {0}", "INIZH.big").Localized);
        }

        [Theory]
        [InlineData(1, "أرشيف واحد يطابق Zero Hour 1.04")]
        [InlineData(2, "أرشيفان يطابقان Zero Hour 1.04")]
        [InlineData(5, "5 أرشيفات تطابق Zero Hour 1.04")]
        [InlineData(11, "11 أرشيفًا يطابق Zero Hour 1.04")]
        [InlineData(100, "100 أرشيف يطابق Zero Hour 1.04")]
        public void Arabic_plural_has_its_own_forms(int count, string expected)
        {
            using var _ = new Arabic();
            Assert.Equal(expected, LocText.N(count, "{0} archive matches Zero Hour 1.04", "{0} archives match Zero Hour 1.04").Localized);
        }

        [Fact]
        public void A_plain_value_is_not_translated_and_stays_left_to_right()
        {
            using var _ = new Arabic();
            Assert.Equal($@"{Ltr}C:\Games\Zero Hour{Ltr}", LocText.Value(@"C:\Games\Zero Hour").Localized);
            Assert.Equal("المستندات", LocText.Value("المستندات").Localized);
        }

        [Fact]
        public void Empty_text_reports_empty()
        {
            Assert.True(((LocText)"").IsEmpty);
            Assert.False(LocText.F("{0}", "x").IsEmpty);
        }
    }
}
