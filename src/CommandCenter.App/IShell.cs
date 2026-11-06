namespace CommandCenter
{
    // What a view model asks of the window (ticket 08): a short message with Undo, the folder dialog, a warning, the
    // update check with its screen, Health's checks again after a change, the Help page and the Tools sections, a yes or
    // no question, and the window itself for Play. MainWindow is the real one; tests use a stand-in, so these outcomes
    // can be checked.
    public interface IShell
    {
        void Toast(string message, Action? undo = null, bool isError = false);

        // The folder the player picked, or null when the dialog was closed
        string? ChooseFolder(string title, string? initial);

        void Warn(string message);

        // Opens the update screen when an update is found
        Task<UpdateCheck> CheckForUpdateAsync();

        void RefreshHealth();

        // The common questions, the support report and where to ask
        void ShowHelp();

        // A Tools section by name: replays, maps, hotkeys or health
        void ShowTools(string? section = null);

        // A yes or no question; true for yes
        bool Confirm(string message);

        // For Play: the window's handle, stepping aside while the game runs without taking the focus from it, and coming
        // back when it closes
        IntPtr WindowHandle { get; }
        void StepAside();
        void ComeBack();
    }
}
