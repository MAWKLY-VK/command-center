using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using CommandCenter.ViewModels;

namespace CommandCenter.Pages.Tools
{
    // Health (ticket 10): the view of HealthViewModel. The page builds the model from the shared services, keeps the
    // time of the last check moving, and answers the Tools page.
    public partial class HealthSection : UserControl, IToolSection
    {
        private readonly HealthViewModel _model;
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

        public HealthSection()
        {
            InitializeComponent();
            _model = new HealthViewModel(AppServices.Health, AppServices.Fixes, AppServices.History, Views.Main, () => DateTime.Now,
                Views.Open, Views.CopySupportReport, OpenBackups);
            DataContext = _model;
            _clock.Tick += (_, _) => _model.RefreshClock();
            Loaded += (_, _) => _clock.Start();
            Unloaded += (_, _) => _clock.Stop();
        }

        public bool HasPendingChanges => false;

        public void OnShown()
        {
            _model.RefreshClock();
            _ = _model.OnShownAsync();
        }

        // For --capture: until the checks are done, and the history shows whether each change can be undone
        public async Task ReadyAsync()
        {
            for (int i = 0; i < 120 && (!AppServices.Health.Report.IsReady || AppServices.Health.IsChecking); i++)
                await Task.Delay(250);
            if (_model.ShowHistory)
                await _model.LoadHistoryAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        public void ShowPart(string part)
        {
            if (part == "history")
                _model.ShowHistory = true;
            else
                _model.ShowChecks = true;
        }

        // Used by --capture with "health:end", to show what passed below the problems
        public void ScrollToEnd()
        {
            ListScroller.UpdateLayout();
            ListScroller.ScrollToEnd();
        }

        // From Help's "Run the checks"
        public Task RunChecksAsync() => _model.RunChecksAsync();

        private static void OpenBackups()
        {
            try
            {
                Directory.CreateDirectory(BackupService.Root);
            }
            catch
            {
                // The folder opens anyway, or Explorer says it is missing
            }
            Views.ShowInFolder(BackupService.Root);
        }
    }
}
