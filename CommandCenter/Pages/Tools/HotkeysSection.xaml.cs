using System.Windows.Controls;

namespace CommandCenter.Pages.Tools
{
    public partial class HotkeysSection : UserControl, IToolSection
    {
        public HotkeysSection()
        {
            InitializeComponent();
        }

        public bool HasPendingChanges => false;
        public void OnShown() { }
        public Task ReadyAsync() => Task.CompletedTask;
        public void ShowPart(string part) { }
    }
}
