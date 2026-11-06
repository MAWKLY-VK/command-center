using System.Text;

namespace CommandCenter.Tests
{
    // Health (ticket 10): one way to apply fixes, for one fix and for Fix all. Each fix is recorded in the operations log
    // and backed up, Undo puts back what changed, a failure does not stop the others, and samples change nothing.
    public sealed class FixRunnerTests : IDisposable
    {
        private readonly TempGame _game = new();
        private int _changed;

        private FixRunner Runner() => new(() => _changed++);

        private HealthResult Writes(string id, string file, string text) => new()
        {
            Id = id, Group = HealthService.GroupFiles, Title = "Fix " + id, Detail = "", Status = HealthStatus.Problem, FixLabel = "Fix",
            Fix = () => BackupService.WriteFile("Fix " + id, Path.Combine(_game.Game, file), Encoding.UTF8.GetBytes(text)),
        };

        private static HealthResult Throws(string title, Exception error) => new()
        {
            Id = "broken", Group = HealthService.GroupFiles, Title = title, Detail = "", Status = HealthStatus.Problem, FixLabel = "Fix",
            Fix = () => throw error,
        };

        [Fact]
        public void A_fix_is_logged_and_backed_up_and_its_undo_puts_the_file_back()
        {
            string file = _game.Write(@"game\Data\INI\Test.ini", "before");
            var runner = Runner();
            var outcome = runner.Apply([Writes("ini", @"Data\INI\Test.ini", "after")]);

            Assert.Equal(("after", 1, 1, 0), (File.ReadAllText(file), outcome.Applied.Count, outcome.Made.Count, outcome.Failed.Count));
            Assert.True(outcome.CanUndo);
            Assert.Equal(1, _changed);
            Assert.Contains(File.ReadAllLines(OperationLog.FilePath), line => line.Contains("\"fix\"") && line.Contains("\"ini\"") && line.Contains("\"done\""));

            runner.Undo(outcome);
            Assert.Equal("before", File.ReadAllText(file));
            Assert.Equal(2, _changed);
        }

        [Fact]
        public void Fixing_several_keeps_going_past_a_failure_and_can_undo_what_worked()
        {
            string file = _game.Write(@"game\Data\INI\Test.ini", "before");
            var runner = Runner();
            var outcome = runner.Apply([Throws("Broken fix", new IOException("The file is in use")), Writes("ini", @"Data\INI\Test.ini", "after")]);

            Assert.Equal(["Broken fix: The file is in use"], outcome.Failed);
            Assert.Equal(["ini"], outcome.Applied.Select(r => r.Id));
            Assert.True(outcome.CanUndo);
            Assert.Contains(File.ReadAllLines(OperationLog.FilePath), line => line.Contains("\"broken\"") && line.Contains("\"failed\""));

            runner.Undo(outcome);
            Assert.Equal("before", File.ReadAllText(file));
        }

        [Fact]
        public void Samples_change_nothing_and_a_fix_that_writes_nothing_offers_no_undo()
        {
            var sample = Throws("Sample", new InvalidOperationException("A sample's fix must not run"));
            var outcome = Runner().Apply([new HealthResult
            {
                Id = sample.Id, Group = sample.Group, Title = sample.Title, Detail = "", Status = sample.Status, FixLabel = "Fix", Fix = sample.Fix,
                IsSample = true,
            }]);
            Assert.True(outcome.OnlySamples);
            Assert.Empty(outcome.Applied);
            Assert.Empty(outcome.Failed);

            var nothing = Runner().Apply([new HealthResult
            {
                Id = "nothing", Group = HealthService.GroupFiles, Title = "Nothing", Detail = "", Status = HealthStatus.Warning, FixLabel = "Fix", Fix = () => { },
            }]);
            Assert.Equal((1, false, false), (nothing.Applied.Count, nothing.CanUndo, nothing.OnlySamples));
            Assert.Equal(0, _changed);
        }

        public void Dispose() => _game.Dispose();
    }
}
