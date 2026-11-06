namespace CommandCenter.Tests
{
    // Health (ticket 10): what the results add up to. The texts and the order are the ones the Health page, the tab's
    // badge and Home showed before the move.
    public sealed class HealthReportTests
    {
        private static HealthResult Result(string id, HealthStatus status, string group = HealthService.GroupFiles, bool fix = false) => new()
        {
            Id = id, Group = group, Title = id, Detail = "", Status = status,
            Fix = fix ? () => { } : null, FixLabel = fix ? "Fix" : null,
        };

        private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0);

        [Fact]
        public void Before_the_first_check_it_says_it_is_checking()
        {
            var report = HealthReport.From([], null);
            Assert.False(report.IsReady);
            Assert.Equal("Checking your game…", report.Summary);
            Assert.Equal((0, 0, 0), (report.Problems, report.Warnings, report.Passed));
            Assert.Empty(report.PassedGroups);
        }

        [Fact]
        public void With_nothing_wrong_it_counts_the_checks_that_passed()
        {
            Assert.Equal("1 check passed. Check again after installing mods or patches.",
                HealthReport.From([Result("a", HealthStatus.Passed)], Now).Summary);
            Assert.Equal("All 3 checks passed. Check again after installing mods or patches.",
                HealthReport.From([Result("a", HealthStatus.Passed), Result("b", HealthStatus.Passed), Result("c", HealthStatus.Passed)], Now).Summary);
        }

        [Fact]
        public void Problems_and_warnings_are_counted_and_named_in_one_sentence()
        {
            var report = HealthReport.From([Result("p1", HealthStatus.Problem), Result("w", HealthStatus.Warning), Result("p2", HealthStatus.Problem), Result("ok", HealthStatus.Passed)], Now);
            Assert.Equal((2, 1, 1, 3), (report.Problems, report.Warnings, report.Passed, report.Issues));
            Assert.Equal("2 problems and 1 warning found. Nothing changes until you press a button, and every fix can be undone.", report.Summary);
            Assert.Equal("1 warning found. Nothing changes until you press a button, and every fix can be undone.",
                HealthReport.From([Result("w", HealthStatus.Warning)], Now).Summary);
        }

        [Fact]
        public void Problems_come_first_then_warnings_then_the_passed_checks_by_area_in_the_order_they_appear()
        {
            var report = HealthReport.From(
            [
                Result("w1", HealthStatus.Warning), Result("ok-win", HealthStatus.Passed, HealthService.GroupWindows),
                Result("p1", HealthStatus.Problem), Result("ok-files", HealthStatus.Passed), Result("p2", HealthStatus.Problem),
                Result("ok-win2", HealthStatus.Passed, HealthService.GroupWindows),
            ], Now);
            Assert.Equal(["p1", "p2"], report.ProblemList.Select(r => r.Id));
            Assert.Equal(["w1"], report.WarningList.Select(r => r.Id));
            Assert.Equal([HealthService.GroupWindows, HealthService.GroupFiles], report.PassedGroups.Select(g => g.Name));
            Assert.Equal(["ok-win", "ok-win2"], report.PassedGroups[0].Results.Select(r => r.Id));
        }

        [Fact]
        public void What_can_be_fixed_is_the_problems_then_the_warnings_that_have_a_fix()
        {
            var report = HealthReport.From(
            [
                Result("w-fix", HealthStatus.Warning, fix: true), Result("p", HealthStatus.Problem),
                Result("p-fix", HealthStatus.Problem, fix: true), Result("ok-fix", HealthStatus.Passed, fix: true),
            ], Now);
            Assert.Equal(["p-fix", "w-fix"], report.Fixable.Select(r => r.Id));
        }

        [Fact]
        public void Home_and_the_badge_read_the_same_counts()
        {
            var clean = HealthReport.From([Result("ok", HealthStatus.Passed)], Now);
            Assert.Equal(("Your game passed every check", 0, false), (clean.HomeLine, clean.BadgeCount, clean.BadgeIsProblem));

            var warned = HealthReport.From([Result("w", HealthStatus.Warning)], Now);
            Assert.Equal(("1 warning found", 1, false), (warned.HomeLine, warned.BadgeCount, warned.BadgeIsProblem));

            var broken = HealthReport.From([Result("p1", HealthStatus.Problem), Result("p2", HealthStatus.Problem), Result("w", HealthStatus.Warning)], Now);
            Assert.Equal(("2 problems and 1 warning found", 3, true), (broken.HomeLine, broken.BadgeCount, broken.BadgeIsProblem));
        }
    }
}
