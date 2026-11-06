namespace CommandCenter
{
    // What a view model asks of the window (ticket 08): a short message with Undo, the folder dialog, a warning, the
    // update check with its screen, and Health's checks again after a change. MainWindow is the real one; tests use a
    // stand-in, so these outcomes can be checked.
    public interface IShell
    {
        void Toast(string message, Action? undo = null, bool isError = false);

        // The folder the player picked, or null when the dialog was closed
        string? ChooseFolder(string title, string? initial);

        void Warn(string message);

        // Opens the update screen when an update is found
        Task<UpdateCheck> CheckForUpdateAsync();

        void RefreshHealth();
    }
}
