using CommandCenter.Pages;

namespace CommandCenter.Tests
{
    // Help's common questions (ticket 07): moved from Health with their order and buttons unchanged
    public sealed class CommonQuestionsTests : IDisposable
    {
        private readonly TempGame _game = new();

        private void InstallGeneralsOnline() => File.WriteAllBytes(Path.Combine(_game.Game, GamePaths.GameExe), []);

        [Fact]
        public void Where_to_get_the_game_comes_first_while_Generals_Online_is_missing() =>
            Assert.Equal("Where do I get the game?", CommonQuestions.All(() => { })[0].Title);

        [Fact]
        public void With_Generals_Online_installed_the_rooms_question_comes_first()
        {
            InstallGeneralsOnline();
            var all = CommonQuestions.All(() => { });
            Assert.Equal("Rooms show greyed out and I cannot join them", all[0].Title);
            Assert.Equal("Where do I get the game?", all[^2].Title);
        }

        [Fact]
        public void Every_question_keeps_its_buttons()
        {
            InstallGeneralsOnline();
            Assert.Equal(
            [
                "Data packs, Run the checks",
                "Camera",
                "Network",
                "Run the checks, Plugins",
                "Show game folder, Run the checks",
                "Run the checks",
                "Run the checks",
                "Replays",
                "Steam, Generals Online, Game folder",
                "Copy support report",
            ], CommonQuestions.All(() => { }).Select(q => string.Join(", ", q.Actions.Select(a => a.Label))));
        }

        [Fact]
        public void Run_the_checks_runs_the_checks_it_is_given()
        {
            int runs = 0;
            CommonQuestions.All(() => runs++).SelectMany(q => q.Actions).First(a => a.Label == "Run the checks").Run();
            Assert.Equal(1, runs);
        }

        public void Dispose() => _game.Dispose();
    }
}
