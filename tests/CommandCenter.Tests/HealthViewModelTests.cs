using System.Text;
using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Health on a view model (ticket 10). These pin what the Health page did before the move: the summary, the order,
    // each result's action, one fix and Fix all with Undo, the samples, the fix history and the time of the last check.
    public sealed class HealthViewModelTests : IDisposable
    {
        private readonly TempGame _game = new();
        private readonly FakeShell _shell = new();
        private readonly List<string> _opened = [];
        private DateTime _now = new(2026, 11, 6, 12, 0, 0);
        private IReadOnlyList<HealthResult> _results = [];
        private Exception? _checkError;
        private int _checks;
        private readonly List<HealthViewModel> _models = [];

        private HealthViewModel Model(out HealthChecks checks)
        {
            checks = new HealthChecks(() =>
            {
                _checks++;
                return _checkError != null ? Task.FromException<IReadOnlyList<HealthResult>>(_checkError) : Task.FromResult(_results);
            }, () => _now);
            var model = new HealthViewModel(checks, new FixRunner(() => { }), new FixHistory(() => { }), _shell, () => _now,
                _opened.Add, () => _opened.Add("report"), () => _opened.Add("backups"));
            // Each model listens to the backup journal, which is shared, until it is disposed
            _models.Add(model);
            return model;
        }

        private static HealthResult Result(string id, HealthStatus status, string group = HealthService.GroupFiles) =>
            new() { Id = id, Group = group, Title = "Title " + id, Detail = "Detail " + id, Status = status };

        private HealthResult Writes(string id, string file, string text, HealthStatus status = HealthStatus.Problem) => new()
        {
            Id = id, Group = HealthService.GroupFiles, Title = "Title " + id, Detail = "", Status = status,
            FixLabel = "Quarantine", FixPreview = "Moves " + file + ". Undo puts it back.",
            Fix = () => BackupService.WriteFile("Changed " + file, Path.Combine(_game.Game, file), Encoding.UTF8.GetBytes(text)),
        };

        [Fact]
        public void Before_the_first_check_it_says_it_is_checking_and_offers_no_fix_all()
        {
            var model = Model(out _);
            Assert.Equal(("Checking your game…", "", false), (model.Summary, model.CheckedAtText, model.CanFixAll));
            Assert.Empty(model.Problems);
        }

        [Fact]
        public async Task Problems_and_warnings_come_first_with_their_actions_and_fix_all_counts_what_can_be_fixed()
        {
            _results =
            [
                Result("plain", HealthStatus.Passed, HealthService.GroupWindows),
                new HealthResult { Id = "faq", Group = HealthService.GroupSettings, Title = "Title faq", Detail = "", Status = HealthStatus.Warning, GuideLabel = "Common questions", GuideTarget = HealthService.QuestionsTarget },
                Writes("dll", "msvcp140.dll", "x"),
                new HealthResult { Id = "folder", Group = HealthService.GroupFiles, Title = "Title folder", Detail = "", Status = HealthStatus.Passed, GuideLabel = "Show folder", GuideTarget = @"select:C:\Game\Maps" },
            ];
            var model = Model(out var checks);
            await checks.CheckAsync();

            Assert.Equal("1 problem and 1 warning found. Nothing changes until you press a button, and every fix can be undone.", model.Summary);
            Assert.Equal(("Title dll", "Quarantine", "wrench"), (model.Problems[0].Title, model.Problems[0].ActionLabel, model.Problems[0].ActionIcon));
            Assert.Equal("Moves msvcp140.dll. Undo puts it back.", model.Problems[0].FixPreview);
            Assert.Equal(("Common questions", "info"), (model.Warnings[0].ActionLabel, model.Warnings[0].ActionIcon));
            Assert.Equal([HealthService.GroupWindows, HealthService.GroupFiles], model.PassedAreas.Select(a => a.Name));
            Assert.Equal((false, true), (model.PassedAreas[0].Items[0].HasAction, model.PassedAreas[1].Items[0].HasAction));
            Assert.Equal("folder", model.PassedAreas[1].Items[0].ActionIcon);
            Assert.Equal(("Apply 1 fix", true), (model.FixAllLabel, model.CanFixAll));
            Assert.Equal("• Moves msvcp140.dll. Undo puts it back.", model.FixAllPreview);

            model.Warnings[0].Action!.Execute(null);
            model.PassedAreas[1].Items[0].Action!.Execute(null);
            Assert.Equal(1, _shell.HelpShown);
            Assert.Equal([@"select:C:\Game\Maps"], _opened);
        }

        [Fact]
        public async Task A_fix_says_what_it_did_and_its_undo_puts_the_change_back()
        {
            string file = _game.Write(@"game\Options.ini", "before");
            _results = [Writes("ini", "Options.ini", "after")];
            var model = Model(out var checks);
            await checks.CheckAsync();

            model.Problems[0].Action!.Execute(null);
            Assert.Equal("after", File.ReadAllText(file));
            var (message, undo, isError) = _shell.Toasts[^1];
            Assert.Equal(("Fixed: Title ini", false), (message, isError));
            Assert.Equal(2, _checks);

            undo!();
            Assert.Equal("before", File.ReadAllText(file));
            Assert.Equal("Change undone", _shell.Toasts[^1].Message);
            Assert.Equal(3, _checks);
        }

        [Fact]
        public async Task Fix_all_names_a_failure_and_still_offers_undo_for_what_worked()
        {
            string file = _game.Write(@"game\Options.ini", "before");
            _results =
            [
                new HealthResult { Id = "broken", Group = HealthService.GroupFiles, Title = "Broken", Detail = "", Status = HealthStatus.Problem, FixLabel = "Fix", Fix = () => throw new IOException("The file is in use") },
                Writes("ini", "Options.ini", "after", HealthStatus.Warning),
            ];
            var model = Model(out var checks);
            await checks.CheckAsync();

            model.FixAllCommand.Execute(null);
            var (message, undo, isError) = _shell.Toasts[^1];
            Assert.Equal(("Some fixes failed: Broken: The file is in use", true), (message, isError));
            Assert.Equal("after", File.ReadAllText(file));
            undo!();
            Assert.Equal(("before", "All fixes undone"), (File.ReadAllText(file), _shell.Toasts[^1].Message));
        }

        [Fact]
        public async Task Fix_all_counts_the_fixes_it_applied()
        {
            _game.Write(@"game\Options.ini", "before");
            _game.Write(@"game\Data\INI\Test.ini", "before");
            _results = [Writes("a", "Options.ini", "after"), Writes("b", @"Data\INI\Test.ini", "after", HealthStatus.Warning)];
            var model = Model(out var checks);
            await checks.CheckAsync();

            model.FixAllCommand.Execute(null);
            Assert.Equal(("Applied 2 fixes.", false), (_shell.Toasts[^1].Message, _shell.Toasts[^1].IsError));
            Assert.NotNull(_shell.Toasts[^1].Undo);
        }

        [Fact]
        public async Task Samples_change_nothing_and_say_so()
        {
            _results = [new HealthResult { Id = "sample:a", Group = HealthService.GroupFiles, Title = "Sample", Detail = "", Status = HealthStatus.Problem, FixLabel = "Fix", Fix = () => throw new InvalidOperationException("must not run"), IsSample = true }];
            var model = Model(out var checks);
            await checks.CheckAsync();

            model.Problems[0].Action!.Execute(null);
            Assert.Equal("This is a sample problem; nothing was changed.", _shell.Toasts[^1].Message);
            model.FixAllCommand.Execute(null);
            Assert.Equal("These are sample problems; nothing was changed.", _shell.Toasts[^1].Message);
        }

        [Fact]
        public async Task The_history_lists_the_changes_and_its_undo_says_what_happened()
        {
            string file = _game.Write(@"game\Options.ini", "before");
            BackupService.WriteFile("Changed Options.ini", file, Encoding.UTF8.GetBytes("after"));
            var model = Model(out _);
            Assert.Equal("Fix history (1)", model.HistoryLabel);

            model.ShowHistory = true;
            await model.LoadHistoryAsync();
            var row = Assert.Single(model.HistoryRows);
            Assert.Equal(("Changed Options.ini", true), (row.Title, row.CanUndo));

            model.UndoCommand.Execute(row);
            Assert.Equal(("before", "Undone: Changed Options.ini"), (File.ReadAllText(file), _shell.Toasts[^1].Message));
        }

        [Fact]
        public async Task The_last_check_follows_the_clock_and_a_failed_check_says_so()
        {
            var model = Model(out var checks);
            await checks.CheckAsync();
            Assert.Equal("Last check: just now", model.CheckedAtText);
            _now = _now.AddMinutes(5);
            model.RefreshClock();
            Assert.Equal("Last check: 5 min ago", model.CheckedAtText);

            _checkError = new IOException("The disk is not ready");
            await model.CheckCommand.ExecuteAsync(null);
            Assert.Equal(("Something went wrong: The disk is not ready", true), (_shell.Toasts[^1].Message, _shell.Toasts[^1].IsError));
        }

        public void Dispose()
        {
            foreach (var model in _models)
                model.Dispose();
            _game.Dispose();
        }
    }
}
