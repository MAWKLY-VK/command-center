namespace CommandCenter.Core.Integrity
{
    // Health's results and when they were checked, for the Health page, the tab's badge, Home and PLAY (ticket 10)
    public interface IHealthChecks
    {
        IReadOnlyList<HealthResult> Results { get; }
        DateTime? CheckedAt { get; }
        bool IsChecking { get; }

        // What the results add up to
        HealthReport Report { get; }

        // A check started or ended
        event Action? Changed;

        // Runs every check; asked again while one runs, it joins that one. A failed check keeps the last results and its
        // error reaches the caller.
        Task CheckAsync();

        // When Health opens: joins a running check, else checks when there are no results or they are older than ten
        // minutes
        Task CheckIfStaleAsync();
    }

    // The one owner of Health's results. run is every check (the local ones, then the network ones); now is the clock.
    public sealed class HealthChecks(Func<Task<IReadOnlyList<HealthResult>>> run, Func<DateTime> now) : IHealthChecks
    {
        // Results older than this may no longer match the files
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

        private Task _running = Task.CompletedTask;

        public IReadOnlyList<HealthResult> Results { get; private set; } = [];
        public DateTime? CheckedAt { get; private set; }
        public bool IsChecking { get; private set; }
        public HealthReport Report => HealthReport.From(Results, CheckedAt);
        public event Action? Changed;

        public Task CheckAsync()
        {
            if (IsChecking)
                return _running;
            IsChecking = true;
            Changed?.Invoke();
            _running = RunAsync();
            return _running;
        }

        public Task CheckIfStaleAsync()
        {
            if (IsChecking)
                return _running;
            if (CheckedAt is { } at && now() - at <= StaleAfter)
                return Task.CompletedTask;
            return CheckAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                var results = await run();
                Results = results;
                CheckedAt = now();
            }
            finally
            {
                IsChecking = false;
                Changed?.Invoke();
            }
        }
    }
}
