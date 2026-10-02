using System.Windows;
using System.Windows.Media;

namespace CommandCenter.Pages
{
    // What the main window needs from a page: its heading, optional top-bar content and a bottom bar.
    public interface IPage
    {
        string Title { get; }
        string Crumb { get; }
        string IconKey { get; }
        FrameworkElement? TopRight { get; }
        FrameworkElement? Footer { get; }

        // Called each time the page is shown
        void OnShown();

        // True while the page holds edits that are not saved yet
        bool HasPendingChanges => false;

        // Ctrl+K: focus the page's search box, if it has one
        void FocusSearch() { }

        // Waits until the page shows real data (used when taking screenshots)
        Task ReadyAsync() => Task.CompletedTask;

        // Switches to a named part of the page, such as a tab (used when taking screenshots)
        void ShowPart(string part) { }
    }
}
