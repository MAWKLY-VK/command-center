using System.Text;

namespace CommandCenter.Tests
{
    // Health's fix history (ticket 10): the changes Command Center made, the newest sixty first, each with an Undo that
    // says what happened. A file changed again since cannot be undone here.
    public sealed class FixHistoryTests : IDisposable
    {
        private readonly TempGame _game = new();
        private int _changed;

        private FixHistory History() => new(() => _changed++);

        private void Change(string file, string text) =>
            BackupService.WriteFile("Changed " + file, Path.Combine(_game.Game, file), Encoding.UTF8.GetBytes(text), "Detail of " + file);

        [Fact]
        public void The_rows_are_the_newest_sixty_changes_newest_first()
        {
            var history = History();
            int heard = 0;
            Action listen = () => heard++;
            history.Changed += listen;
            try
            {
                for (int i = 1; i <= 61; i++)
                    Change($"File{i}.ini", "after");
            }
            finally
            {
                history.Changed -= listen;
            }

            var rows = history.Rows();
            Assert.Equal(60, rows.Count);
            Assert.Equal(("Changed File61.ini", "Detail of File61.ini"), (rows[0].Title, rows[0].Detail));
            Assert.Equal("Changed File2.ini", rows[^1].Title);
            Assert.True(heard >= 61);
        }

        [Fact]
        public void Undo_puts_the_file_back_and_says_so()
        {
            string file = _game.Write(@"game\Options.ini", "before");
            Change("Options.ini", "after");
            var history = History();
            var row = Assert.Single(history.Rows());

            Assert.True(history.CanUndo(row));
            var outcome = history.Undo(row);
            Assert.Equal((true, "Undone: Changed Options.ini"), (outcome.Undone, outcome.Message));
            Assert.Equal("before", File.ReadAllText(file));
            Assert.Equal(1, _changed);
        }

        [Fact]
        public void A_file_changed_again_since_cannot_be_undone_here()
        {
            string file = _game.Write(@"game\Options.ini", "before");
            Change("Options.ini", "after");
            File.WriteAllText(file, "changed by the game");
            var history = History();
            var row = Assert.Single(history.Rows());

            Assert.False(history.CanUndo(row));
            var outcome = history.Undo(row);
            Assert.Equal((false, "Changed again since, so this cannot be undone here"), (outcome.Undone, outcome.Message));
            Assert.Equal("changed by the game", File.ReadAllText(file));
            Assert.Equal(0, _changed);
        }

        public void Dispose() => _game.Dispose();
    }
}
