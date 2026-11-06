using System.Globalization;
using CommandCenter.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommandCenter.ViewModels
{
    // A problem, a warning or a passed check on the Health page, with the one action it offers: its fix, or a guide
    public sealed class HealthItem(HealthResult result, string? actionLabel, string? actionIcon, IRelayCommand? action)
    {
        public HealthResult Result { get; } = result;
        public string Title => Result.Title;
        public string Detail => Result.Detail;
        public bool HasDetail => Result.Detail.Length > 0;
        public string? Note => Result.Note;
        public bool HasNote => Result.Note != null;
        public bool IsProblem => Result.Status == HealthStatus.Problem;

        // What the fix will change, shown before it is pressed
        public string? FixPreview => Result.Fix != null && Result.Status != HealthStatus.Passed ? Result.FixPreview : null;
        public bool HasFixPreview => FixPreview != null;

        public string? ActionLabel { get; } = actionLabel;
        public string? ActionIcon { get; } = actionIcon;
        public IRelayCommand? Action { get; } = action;
        public bool HasAction => Action != null;
    }

    // The passed checks of one area
    public sealed record HealthArea(string Name, IReadOnlyList<HealthItem> Items);

    // One change in the fix history, with whether it can still be undone (found out after the list shows)
    public sealed partial class HistoryRow(FixHistoryRow row, string ago, string date) : ObservableObject
    {
        public FixHistoryRow Row { get; } = row;
        public string Title => Row.Title;
        public string Detail => Row.Detail;
        public bool HasDetail => Row.Detail.Length > 0;
        public string Ago { get; } = ago;
        public string Date { get; } = date;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UndoTip))]
        private bool _canUndo;

        public string UndoTip => CanUndo ? "Put back what was there before" : "Changed again since, so this cannot be undone here";
    }

    // Health on a view model (ticket 10): the checks that need attention first, each with its fix or guide, then what
    // passed by area; Fix all; and the history of changes, each with Undo. The results come from IHealthChecks, the
    // fixes go through FixRunner, and the history through FixHistory.
    public sealed partial class HealthViewModel : ObservableObject, IDisposable
    {
        private readonly IHealthChecks _checks;
        private readonly FixRunner _fixes;
        private readonly FixHistory _history;
        private readonly IShell _shell;
        private readonly Func<DateTime> _now;
        private readonly Action<string> _open;
        private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
        private readonly Action _onChecks;
        private readonly Action _onHistory;
        private HealthReport _report;
        private bool _showHistory;
        private bool _historyStale = true;
        private bool _historyQueued;
        private int _historyVersion;
        private int _historyCount;

        public HealthViewModel(IHealthChecks checks, FixRunner fixes, FixHistory history, IShell shell, Func<DateTime> now,
            Action<string> open, Action copyReport, Action openBackups)
        {
            _checks = checks;
            _fixes = fixes;
            _history = history;
            _shell = shell;
            _now = now;
            _open = open;
            _report = checks.Report;
            CopyReportCommand = new RelayCommand(copyReport);
            OpenBackupsCommand = new RelayCommand(openBackups);
            _onChecks = () => OnUi(Show);
            _onHistory = () => OnUi(QueueHistory);
            checks.Changed += _onChecks;
            history.Changed += _onHistory;
            Show();
            _historyCount = history.Rows().Count;
        }

        public IRelayCommand CopyReportCommand { get; }
        public IRelayCommand OpenBackupsCommand { get; }

        // ── Checks ──

        public string Summary => _report.Summary;
        public bool IsReady => _report.IsReady;
        public bool IsLoading => !_report.IsReady;
        public bool IsChecking => _checks.IsChecking;
        public IReadOnlyList<HealthItem> Problems { get; private set; } = [];
        public IReadOnlyList<HealthItem> Warnings { get; private set; } = [];
        public IReadOnlyList<HealthArea> PassedAreas { get; private set; } = [];
        public bool HasProblems => Problems.Count > 0;
        public bool HasWarnings => Warnings.Count > 0;
        public bool HasPassed => PassedAreas.Count > 0;
        public string PassedTitle => English.Plural(_report.Passed, "{0} check passed", "{0} checks passed");

        public bool CanFixAll => _report.Fixable.Count > 0;
        // Names what it does, so a problem whose action is a guide is plainly not counted
        public string FixAllLabel => English.Plural(_report.Fixable.Count, "Apply {0} fix", "Apply {0} fixes");
        public string? FixAllPreview => CanFixAll ? string.Join("\n", _report.Fixable.Select(f => "• " + (f.FixPreview ?? f.Title))) : null;

        public string CheckedAtText => _checks.IsChecking ? "Checking…"
            : _report.CheckedAt is { } at ? string.Format("Last check: {0}", Views.Ago(at, _now()))
            : "";

        // The time of the last check moves on; the page asks every so often
        public void RefreshClock() => OnPropertyChanged(nameof(CheckedAtText));

        // Health opened: a running check is joined, old or missing results are checked again
        public Task OnShownAsync() => Guarded(_checks.CheckIfStaleAsync);

        // Help's "Run the checks": the checks, run again
        public Task RunChecksAsync()
        {
            ShowChecks = true;
            return CheckCommand.ExecuteAsync(null);
        }

        [RelayCommand]
        private Task CheckAsync() => Guarded(_checks.CheckAsync);

        private void Show()
        {
            _report = _checks.Report;
            Problems = _report.ProblemList.Select(Item).ToList();
            Warnings = _report.WarningList.Select(Item).ToList();
            PassedAreas = _report.PassedGroups.Select(g => new HealthArea(g.Name, g.Results.Select(Item).ToList())).ToList();
            OnPropertyChanged(string.Empty);
        }

        // A result's action: its fix while it is not passed, else its guide (the common questions open Help)
        private HealthItem Item(HealthResult result)
        {
            if (result.Fix != null && result.FixLabel != null && result.Status != HealthStatus.Passed)
                return new HealthItem(result, result.FixLabel, "wrench", new RelayCommand(() => Fix(result)));
            if (result.GuideTarget is { } target && result.GuideLabel is { } label)
            {
                if (target == HealthService.QuestionsTarget)
                    return new HealthItem(result, label, "info", new RelayCommand(_shell.ShowHelp));
                string icon = target.StartsWith("select:", StringComparison.Ordinal) ? "folder" : "external";
                return new HealthItem(result, label, icon, new RelayCommand(() => _open(target)));
            }
            return new HealthItem(result, null, null, null);
        }

        private void Fix(HealthResult result) => FixActions.ApplyOne(_fixes, _shell, result, Recheck);

        // The fixes that worked can be undone even when others failed
        [RelayCommand]
        private void FixAll()
        {
            if (_report.Fixable.Count == 0)
                return;
            var outcome = _fixes.Apply(_report.Fixable);
            if (outcome.OnlySamples)
            {
                _shell.Toast("These are sample problems; nothing was changed.");
                return;
            }
            Action? undo = outcome.CanUndo ? () => UndoFixes(outcome, "All fixes undone") : null;
            if (outcome.Failed.Count > 0)
                _shell.Toast(string.Format("Some fixes failed: {0}", string.Join("; ", outcome.Failed)), undo, isError: true);
            else
                _shell.Toast(English.Plural(outcome.Applied.Count, "Applied {0} fix.", "Applied {0} fixes."), undo);
            _ = Recheck();
        }

        private void UndoFixes(FixOutcome outcome, string message)
        {
            string? failure = _fixes.Undo(outcome);
            _ = Recheck();
            _shell.Toast(failure ?? message, isError: failure != null);
        }

        private Task Recheck() => Guarded(_checks.CheckAsync);

        private async Task Guarded(Func<Task> check)
        {
            try
            {
                await check();
            }
            catch (Exception ex)
            {
                _shell.Toast(string.Format("Something went wrong: {0}", ex.Message), isError: true);
            }
        }

        // ── Fix history ──

        public bool ShowChecks
        {
            get => !_showHistory;
            set
            {
                if (value)
                    SetTab(history: false);
            }
        }

        public bool ShowHistory
        {
            get => _showHistory;
            set
            {
                if (value)
                    SetTab(history: true);
            }
        }

        public IReadOnlyList<HistoryRow> HistoryRows { get; private set; } = [];
        public bool HasHistory => _historyCount > 0;
        public bool NoHistory => _historyCount == 0;
        public string HistoryLabel => _historyCount > 0 ? string.Format("Fix history ({0})", _historyCount) : "Fix history";

        private void SetTab(bool history)
        {
            if (_showHistory == history)
                return;
            _showHistory = history;
            OnPropertyChanged(nameof(ShowChecks));
            OnPropertyChanged(nameof(ShowHistory));
            if (history && _historyStale)
                _ = LoadHistoryAsync();
        }

        // Backups change in bursts (one entry per file), so the list is read again once the burst is over
        private void QueueHistory()
        {
            _historyStale = true;
            if (_historyQueued)
                return;
            _historyQueued = true;
            Later(() =>
            {
                _historyQueued = false;
                _ = LoadHistoryAsync();
            });
        }

        // The rows show at once; whether each can still be undone means reading its file, so that comes after, off the
        // UI thread
        public async Task LoadHistoryAsync()
        {
            var rows = _history.Rows();
            _historyCount = rows.Count;
            OnPropertyChanged(nameof(HistoryLabel));
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(NoHistory));
            if (!_showHistory)
            {
                _historyStale = true;
                return;
            }
            _historyStale = false;
            int version = ++_historyVersion;
            var now = _now();
            HistoryRows = rows.Select(r => new HistoryRow(r, Views.Ago(r.When, now), r.When.ToString("g", CultureInfo.CurrentCulture))).ToList();
            OnPropertyChanged(nameof(HistoryRows));
            var shown = HistoryRows;
            var undoable = await Task.Run(() => rows.Select(_history.CanUndo).ToList());
            if (version != _historyVersion)
                return;
            for (int i = 0; i < shown.Count; i++)
                shown[i].CanUndo = undoable[i];
        }

        [RelayCommand]
        private void Undo(HistoryRow row)
        {
            var outcome = _history.Undo(row.Row);
            _shell.Toast(outcome.Message, isError: !outcome.Undone);
            _ = Recheck();
        }

        // ── Threads ──

        // Results and backups can be announced from another thread; the model changes on the thread that made it
        private void OnUi(Action action)
        {
            if (_ui == null || SynchronizationContext.Current == _ui)
                action();
            else
                _ui.Post(_ => action(), null);
        }

        // After what runs now on the model's thread has finished
        private void Later(Action action)
        {
            if (_ui == null)
                action();
            else
                _ui.Post(_ => action(), null);
        }

        public void Dispose()
        {
            _checks.Changed -= _onChecks;
            _history.Changed -= _onHistory;
        }
    }
}
