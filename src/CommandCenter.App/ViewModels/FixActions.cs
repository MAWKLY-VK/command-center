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
                    fixes.Undo(outcome);
                    _ = recheck();
                    shell.Toast("Change undone");
                } : null);
            _ = recheck();
        }
    }
}
