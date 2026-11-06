using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Settings, part 1 (ticket 08): the game folder, the launcher and Appearance on view models. The game folder and
    // launcher tests pin what the Options page showed and did before the move; the update check now also says when it
    // could not run.
    public sealed class SettingsViewModelTests
    {
        private readonly FakeGameFolder _game = new();
        private readonly FakeShell _shell = new();

        private GameFolderViewModel GameFolder() => new(_game, _shell, () => "100326", () => @"C:\Users\Player\Documents\Data");

        // ── Game folder ──

        [Fact]
        public void The_game_folder_says_where_it_is_where_it_came_from_and_what_is_in_it()
        {
            var model = GameFolder();
            Assert.Equal(@"C:\Games\Zero Hour", model.Folder);
            Assert.Equal("Other copy", model.Source);
            Assert.Equal(["Zero Hour found", "Generals Online 100326 installed"], model.Found);
            Assert.Empty(model.Missing);
            Assert.Equal("Any copy works: Steam, EA or another install. Changing the folder changes nothing in the game files.", model.Note);
            Assert.Equal(@"C:\Users\Player\Documents\Data", model.DataFolder);
        }

        [Theory]
        [InlineData(GameFolderSource.ChosenByPlayer, "Chosen by you")]
        [InlineData(GameFolderSource.Steam, "Steam copy")]
        public void The_source_is_named(GameFolderSource source, string name)
        {
            _game.Source = source;
            Assert.Equal(name, GameFolder().Source);
        }

        [Fact]
        public void A_copy_without_Generals_Online_asks_for_it()
        {
            _game.GeneralsOnlineFound = false;
            var model = GameFolder();
            Assert.Equal(["Generals Online is not installed"], model.Missing);
            Assert.Equal("Install Generals Online into this folder, then press Play.", model.Note);
        }

        [Fact]
        public void Without_the_game_it_says_so_and_how_to_find_it()
        {
            _game.ZeroHourFound = false;
            _game.Source = GameFolderSource.NotFound;
            var model = GameFolder();
            Assert.Equal(("Game not found", "Not found"), (model.Folder, model.Source));
            Assert.Empty(model.Found);
            Assert.Equal("Choose the folder of any copy of Zero Hour: Steam, EA or another install.", model.Note);
        }

        [Fact]
        public void Changing_the_folder_uses_it_and_says_the_checks_run_again()
        {
            _shell.FolderToPick = @"D:\Zero Hour";
            var model = GameFolder();
            model.ChangeFolderCommand.Execute(null);
            Assert.Equal([@"D:\Zero Hour"], _game.Chosen);
            Assert.Equal(@"D:\Zero Hour", model.Folder);
            Assert.Equal("Game folder changed. The checks run again.", Assert.Single(_shell.Toasts).Message);
        }

        [Fact]
        public void A_folder_without_Zero_Hour_is_refused_with_a_warning()
        {
            _shell.FolderToPick = @"D:\Photos";
            _game.Accept = false;
            GameFolder().ChangeFolderCommand.Execute(null);
            Assert.Equal("That folder does not hold Zero Hour. Choose the folder with WindowZH.big and INIZH.big in it.", Assert.Single(_shell.Warnings));
            Assert.Empty(_shell.Toasts);
        }

        [Fact]
        public void Closing_the_dialog_changes_nothing()
        {
            GameFolder().ChangeFolderCommand.Execute(null);
            Assert.Empty(_game.Chosen);
            Assert.Empty(_shell.Toasts);
        }

        // ── Launcher ──

        [Fact]
        public async Task Check_for_updates_tells_the_latest_version_a_new_one_and_a_check_that_could_not_run_apart()
        {
            var model = new LauncherViewModel(_shell, "0.5");
            Assert.Equal("Command Center 0.5", model.Version);

            Assert.False(model.ShowUpdateStatus);
            await model.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.Equal("You have the latest version.", model.UpdateStatus);
            Assert.True(model.ShowUpdateStatus);

            _shell.UpdateAnswer = new UpdateCheck(null, "The update information could not be read. Check the internet connection.");
            await model.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.Equal("The update information could not be read. Check the internet connection.", model.UpdateStatus);

            _shell.UpdateAnswer = new UpdateCheck(new UpdateInfo(new Version(0, 6, 0), "0.6.0", "https://example.invalid", "", ""), null);
            await model.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.Equal("", model.UpdateStatus);
        }

        // ── Appearance ──

        private sealed class FakeAppearance : IAppearance
        {
            public HomeImage HomeImage { get; set; }
            public AnimationSetting Animations { get; set; }
            public bool InterfaceSounds { get; set; }
            public bool AnimationsOn => Animations != AnimationSetting.Off;
            public event Action? Changed { add { } remove { } }
        }

        [Fact]
        public void Appearance_choices_reach_the_settings()
        {
            var appearance = new FakeAppearance();
            var model = new AppearanceViewModel(appearance);
            Assert.True(model.Pictures.Single(p => p.Value == HomeImage.Changing).Selected);

            model.Pictures.Single(p => p.Value == HomeImage.China).Selected = true;
            model.Animations.Single(a => a.Value == AnimationSetting.Off).Selected = true;
            model.InterfaceSounds = true;

            Assert.Equal((HomeImage.China, AnimationSetting.Off, true), (appearance.HomeImage, appearance.Animations, appearance.InterfaceSounds));
        }
    }
}
