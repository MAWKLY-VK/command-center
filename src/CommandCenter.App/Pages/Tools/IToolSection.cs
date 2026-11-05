namespace CommandCenter.Pages.Tools
{
    // A section of the Tools page (Health, Map library, Replays, Hotkeys, Add-ons).
    public interface IToolSection
    {
        // Called each time the section is shown
        void OnShown();

        // True while the section holds edits that are not saved yet
        bool HasPendingChanges { get; }

        // Waits until the section shows real data (used when taking screenshots)
        Task ReadyAsync();

        // Switches to a named part of the section, such as a tab (used when taking screenshots)
        void ShowPart(string part);
    }
}
