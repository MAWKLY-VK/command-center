namespace CommandCenter.Core.Safety
{
    // One change in the fix history: when, what, and the backup Undo puts back
    public sealed record FixHistoryRow(DateTime When, string Title, string Detail, BackupEntry Entry);

    // What an Undo did, in words for the player
    public sealed record UndoOutcome(bool Undone, string Message);

    // The changes Command Center made, for Health's fix history (ticket 10): the newest sixty first, each with an Undo.
    // changed runs after a change was put back (the hotkey editor reads the game's text table, which Undo can change).
    public sealed class FixHistory(Action changed)
    {
        public const int Shown = 60;

        // The journal changed (it changes in bursts, one entry per file)
        public event Action? Changed
        {
            add => BackupService.Changed += value;
            remove => BackupService.Changed -= value;
        }

        // Newest first; changes made in the same instant keep the order they were made in, newest first
        public IReadOnlyList<FixHistoryRow> Rows() => BackupService.Load()
            .Select((entry, index) => (Entry: entry, Index: index))
            .OrderByDescending(e => e.Entry.When).ThenByDescending(e => e.Index)
            .Take(Shown)
            .Select(e => new FixHistoryRow(e.Entry.When, e.Entry.Title, e.Entry.Detail, e.Entry))
            .ToList();

        // Reads the file the change wrote, so callers run it off the UI thread
        public bool CanUndo(FixHistoryRow row) => BackupService.CanUndo(row.Entry);

        public UndoOutcome Undo(FixHistoryRow row)
        {
            try
            {
                if (!BackupService.Undo(row.Entry))
                    return new UndoOutcome(false, "Changed again since, so this cannot be undone here");
                changed();
                return new UndoOutcome(true, string.Format("Undone: {0}", row.Title));
            }
            catch (Exception ex)
            {
                return new UndoOutcome(false, string.Format("Something went wrong: {0}", ex.Message));
            }
        }
    }
}
