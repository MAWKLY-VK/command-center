using System.Windows.Controls;

namespace CommandCenter.Pages.Tools
{
    public partial class HealthSection : UserControl, IToolSection
    {
        public HealthSection()
        {
            InitializeComponent();
        }

        public bool HasPendingChanges => false;
        public void OnShown() { }
        public Task ReadyAsync() => Task.CompletedTask;
        public void ShowPart(string part) { }
    }
}
