namespace CommandCenter.ViewModels
{
    // One Health fix pressed on a page (Health's rows, Home's Fix): applied through FixRunner, said in a message with
    // Undo, and the checks run again after it
    internal static class FixActions
    {
        public static void ApplyOne(FixRunner fixes, IShell shell, HealthResult result, Func<Task> recheck)
        {
            var outcome = fixes.Apply([result]);
            if (outcome.OnlySamples)
            {
                shell.Toast("This is a sample problem; nothing was changed.");
                return;
            }
            if (outcome.Failed.Count > 0)
                shell.Toast(outcome.Failed[0], isError: true);
            else
                shell.Toast(string.Format("Fixed: {0}", result.Title), outcome.CanUndo ? () =>
                {
                    string? failure = fixes.Undo(outcome);
                    _ = recheck();
                    shell.Toast(failure ?? "Change undone", isError: failure != null);
                } : null);
            _ = recheck();
        }

        // A fix that takes time (a download): what it is doing as it goes, then a message with Undo, as for the others
        public static async Task ApplyOneAsync(FixRunner fixes, IShell shell, HealthResult result, Func<Task> recheck)
        {
            if (result.IsSample)
            {
                shell.Toast("This is a sample problem; nothing was changed.");
                return;
            }
            try
            {
                var made = await result.FixAsync!(new Progress<string>(text => shell.Toast(text)));
                var outcome = new FixOutcome([result], [], made, OnlySamples: false);
                shell.Toast(string.Format("{0}: done", result.FixLabel), outcome.CanUndo ? () =>
                {
                    string? failure = fixes.Undo(outcome);
                    _ = recheck();
                    shell.Toast(failure ?? "Change undone", isError: failure != null);
                } : null);
            }
            catch (Exception ex)
            {
                shell.Toast(string.Format("{0}: {1}", result.FixLabel, ex.Message), isError: true);
            }
            _ = recheck();
        }
    }
}
