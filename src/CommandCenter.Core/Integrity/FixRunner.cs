namespace CommandCenter.Core.Integrity
{
    // What applying fixes did: the fixes that ran, those that failed with why, and the backups they made, which Undo puts
    // back. OnlySamples: everything asked for was a made-up problem (--samples), so nothing ran.
    public sealed record FixOutcome(IReadOnlyList<HealthResult> Applied, IReadOnlyList<string> Failed, IReadOnlyList<BackupEntry> Made, bool OnlySamples)
    {
        public bool CanUndo => Made.Count > 0;
    }

    // Applies Health's fixes (ticket 10), one or many the same way: each through the operations log, each change backed
    // up by BackupService, a failure not stopping the others. changed runs after files were changed or put back (the
    // hotkey editor reads the game's text table, which a fix can change).
    public sealed class FixRunner(Action changed)
    {
        public FixOutcome Apply(IReadOnlyList<HealthResult> results)
        {
            var real = results.Where(r => r.Fix != null && !r.IsSample).ToList();
            if (real.Count == 0)
                return new FixOutcome([], [], [], OnlySamples: results.Any(r => r.IsSample));

            int before = BackupService.Load().Count;
            var applied = new List<HealthResult>();
            var failed = new List<string>();
            foreach (var result in real)
            {
                try
                {
                    OperationLog.Record("fix", result.Id, result.Title, result.Fix!);
                    applied.Add(result);
                }
                catch (Exception ex)
                {
                    failed.Add($"{result.Title}: {ex.Message}");
                }
            }
            var made = BackupService.Load().Skip(before).ToList();
            if (made.Count > 0)
                changed();
            return new FixOutcome(applied, failed, made, OnlySamples: false);
        }

        // Puts back what the fixes changed, the newest change first. Null when all of it was put back; otherwise why a
        // change stayed (changed again since, or the game is running), for the player to read instead of "undone".
        public string? Undo(FixOutcome outcome)
        {
            int undone = 0;
            string? failure = null;
            foreach (var entry in Enumerable.Reverse(outcome.Made))
            {
                try
                {
                    if (BackupService.Undo(entry))
                        undone++;
                    else
                        failure ??= "Changed again since, so this cannot be undone here";
                }
                catch (Exception ex)
                {
                    failure ??= ex.Message;
                }
            }
            if (undone > 0)
                changed();
            return failure;
        }
    }
}
