using System.Text.Json;

namespace CommandCenter.Tests
{
    // Appearance (ticket 08): Home's picture, animation effects and interface sounds in one module, stored with the other
    // settings
    public sealed class AppearanceTests : IDisposable
    {
        private readonly TempGame _game = new();

        [Theory]
        [InlineData(AnimationSetting.FollowWindows, true, true)]
        [InlineData(AnimationSetting.FollowWindows, false, false)]
        [InlineData(AnimationSetting.On, false, true)]
        [InlineData(AnimationSetting.Off, true, false)]
        public void Animation_effects_follow_Windows_unless_they_are_set(AnimationSetting setting, bool windows, bool on)
        {
            var appearance = new Appearance(() => windows) { Animations = setting };
            Assert.Equal(on, appearance.AnimationsOn);
        }

        [Fact]
        public void Settings_from_0_4_0_follow_Windows_change_the_picture_and_keep_sounds_off()
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("""{"GameFolder":null,"IconLetters":true}""")!;
            Assert.Equal(AnimationSetting.FollowWindows, settings.Animations);
            Assert.Equal(HomeImage.Changing, settings.HomeImage);
            Assert.False(settings.InterfaceSounds);
        }

        [Fact]
        public void An_animation_setting_this_version_does_not_know_follows_Windows() =>
            Assert.Equal(AnimationSetting.FollowWindows,
                JsonSerializer.Deserialize<AppSettings>("""{"AnimationsName":"Sometimes"}""")!.Animations);

        [Fact]
        public void A_change_is_saved_and_announced()
        {
            var appearance = new Appearance(() => true);
            int changes = 0;
            appearance.Changed += () => changes++;

            appearance.InterfaceSounds = true;
            appearance.HomeImage = HomeImage.Gla;

            var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(GamePaths.AppData, "settings.json")))!;
            Assert.True(saved.InterfaceSounds);
            Assert.Equal(HomeImage.Gla, saved.HomeImage);
            Assert.Equal(2, changes);
        }

        public void Dispose()
        {
            AppSettings.Current.Animations = AnimationSetting.FollowWindows;
            AppSettings.Current.InterfaceSounds = false;
            AppSettings.Current.HomeImage = HomeImage.Changing;
            _game.Dispose();
        }
    }
}
