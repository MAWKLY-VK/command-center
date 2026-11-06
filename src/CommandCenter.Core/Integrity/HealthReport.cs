namespace CommandCenter.Core.Integrity
{
    // One group of results on the Health page: the passed checks of one area
    public sealed record HealthGroup(string Name, IReadOnlyList<HealthResult> Results);

    // What Health's results add up to (ticket 10): the counts, the sentence under the title, the order the page shows
    // them in, what Fix all would fix, Home's line and the tab's badge. Before the first check it says it is checking.
    public sealed class HealthReport
    {
        private HealthReport(IReadOnlyList<HealthResult> results, DateTime? checkedAt)
        {
            CheckedAt = checkedAt;
            ProblemList = results.Where(r => r.Status == HealthStatus.Problem).ToList();
            WarningList = results.Where(r => r.Status == HealthStatus.Warning).ToList();
            var passed = results.Where(r => r.Status == HealthStatus.Passed).ToList();
            Passed = passed.Count;
            // GroupBy keeps the order in which each area first appears, which is the order the checks run in
            PassedGroups = passed.GroupBy(r => r.Group).Select(g => new HealthGroup(g.Key, g.ToList())).ToList();
            Fixable = ProblemList.Concat(WarningList).Where(r => r.Fix != null).ToList();
            Summary = Sentence(results.Count);
        }

        public static HealthReport From(IReadOnlyList<HealthResult> results, DateTime? checkedAt) => new(results, checkedAt);

        public DateTime? CheckedAt { get; }
        public bool IsReady => CheckedAt != null;

        public IReadOnlyList<HealthResult> ProblemList { get; }
        public IReadOnlyList<HealthResult> WarningList { get; }
        public IReadOnlyList<HealthGroup> PassedGroups { get; }

        // The problems, then the warnings, that have a fix
        public IReadOnlyList<HealthResult> Fixable { get; }

        public int Problems => ProblemList.Count;
        public int Warnings => WarningList.Count;
        public int Passed { get; }
        public int Issues => Problems + Warnings;

        public string Summary { get; }

        // The tab's badge: how many need attention, in the problem colour when any is a problem
        public int BadgeCount => Issues;
        public bool BadgeIsProblem => Problems > 0;

        // Home's Health panel, under its title
        public string HomeLine => Issues == 0 ? "Your game passed every check" : Found() + " found";

        private string Sentence(int all)
        {
            if (!IsReady)
                return "Checking your game…";
            if (Issues == 0)
                return English.Plural(all, "{0} check passed. Check again after installing mods or patches.",
                    "All {0} checks passed. Check again after installing mods or patches.");
            return string.Format("{0} found.", Found()) + " Nothing changes until you press a button, and every fix can be undone.";
        }

        // "2 problems and 1 warning"
        private string Found()
        {
            var found = new List<string>();
            if (Problems > 0)
                found.Add(English.Plural(Problems, "{0} problem", "{0} problems"));
            if (Warnings > 0)
                found.Add(English.Plural(Warnings, "{0} warning", "{0} warnings"));
            return English.List(found);
        }
    }
}
