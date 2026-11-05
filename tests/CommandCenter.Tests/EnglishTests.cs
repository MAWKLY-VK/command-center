using System.Globalization;

namespace CommandCenter.Tests
{
    // The wording of counts and lists, pinned to what the program has always shown in English (the LocText tests)
    public class EnglishTests
    {
        [Theory]
        [InlineData(new[] { "a.big" }, "a.big")]
        [InlineData(new[] { "a.big", "b.big" }, "a.big and b.big")]
        [InlineData(new[] { "a.big", "b.big", "c.big" }, "a.big, b.big and c.big")]
        [InlineData(new[] { "a.big", "b.big", "c.big", "d.big", "e.big" }, "a.big, b.big, c.big, d.big and e.big")]
        public void Names_are_listed_with_and_before_the_last(string[] names, string expected) =>
            Assert.Equal(expected, English.List(names));

        [Theory]
        [InlineData(new[] { "a.big", "b.big", "c.big" }, "a.big, b.big and c.big")]
        [InlineData(new[] { "a.big", "b.big", "c.big", "d.big", "e.big" }, "a.big, b.big, c.big and 2 more")]
        public void A_long_list_shows_the_first_names_and_how_many_more(string[] names, string expected) =>
            Assert.Equal(expected, English.List(names, show: 3));

        [Fact]
        public void No_names_give_empty_text() => Assert.Equal("", English.List([]));

        [Fact]
        public void Plural_uses_one_only_for_a_single_item()
        {
            const string one = "{0} archive matches Zero Hour 1.04", other = "{0} archives match Zero Hour 1.04";
            Assert.Equal("1 archive matches Zero Hour 1.04", English.Plural(1, one, other));
            Assert.Equal("3 archives match Zero Hour 1.04", English.Plural(3, one, other));
            Assert.Equal("0 archives match Zero Hour 1.04", English.Plural(0, one, other));
        }

        [Fact]
        public void Plural_text_takes_more_values_after_the_count() =>
            Assert.Equal("2 maps in Downloads", English.Plural(2, "{0} map in {1}", "{0} maps in {1}", "Downloads"));

        [Fact]
        public void Plural_text_writes_numbers_the_way_windows_does()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal("1.500 maps", English.Plural(1500, "{0:N0} map", "{0:N0} maps"));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
