namespace CommandCenter.Tests
{
    // Health (ticket 10): the one owner of the results. It runs one check at a time, keeps the last results when a check
    // fails, and checks again on opening Health only when the results are missing or older than ten minutes.
    public sealed class HealthChecksTests
    {
        private DateTime _now = new(2026, 11, 6, 12, 0, 0);
        private int _runs;
        private TaskCompletionSource<IReadOnlyList<HealthResult>> _pending = new();

        private static HealthResult Result(string id) => new() { Id = id, Group = HealthService.GroupFiles, Title = id, Detail = "", Status = HealthStatus.Passed };

        private HealthChecks Checks() => new(() =>
        {
            _runs++;
            return _pending.Task;
        }, () => _now);

        [Fact]
        public async Task The_results_and_their_time_arrive_together_and_listeners_hear_the_start_and_the_end()
        {
            var checks = Checks();
            var heard = new List<bool>();
            checks.Changed += () => heard.Add(checks.IsChecking);

            var check = checks.CheckAsync();
            Assert.True(checks.IsChecking);
            Assert.Null(checks.CheckedAt);
            _pending.SetResult([Result("a")]);
            await check;

            Assert.Equal(["a"], checks.Results.Select(r => r.Id));
            Assert.Equal(_now, checks.CheckedAt);
            Assert.False(checks.IsChecking);
            Assert.Equal([true, false], heard);
            Assert.True(checks.Report.IsReady);
        }

        [Fact]
        public async Task Asking_again_while_checking_joins_the_check_that_is_running()
        {
            var checks = Checks();
            var first = checks.CheckAsync();
            var second = checks.CheckAsync();
            var opened = checks.CheckIfStaleAsync();
            _pending.SetResult([Result("a")]);
            await Task.WhenAll(first, second, opened);
            Assert.Equal(1, _runs);
        }

        [Fact]
        public async Task Opening_Health_checks_only_when_there_are_no_results_or_they_are_old()
        {
            var checks = Checks();
            _pending.SetResult([Result("a")]);
            await checks.CheckIfStaleAsync();
            Assert.Equal(1, _runs);

            _now = _now.AddMinutes(5);
            await checks.CheckIfStaleAsync();
            Assert.Equal(1, _runs);

            _now = _now.AddMinutes(6);
            await checks.CheckIfStaleAsync();
            Assert.Equal(2, _runs);
        }

        [Fact]
        public async Task A_check_that_fails_keeps_the_last_results_and_the_caller_hears_why()
        {
            var checks = Checks();
            _pending.SetResult([Result("a")]);
            await checks.CheckAsync();
            var checkedAt = checks.CheckedAt;

            _pending = new();
            _pending.SetException(new IOException("The disk is not ready"));
            _now = _now.AddMinutes(1);
            var error = await Assert.ThrowsAsync<IOException>(checks.CheckAsync);

            Assert.Equal("The disk is not ready", error.Message);
            Assert.Equal(["a"], checks.Results.Select(r => r.Id));
            Assert.Equal(checkedAt, checks.CheckedAt);
            Assert.False(checks.IsChecking);
        }
    }
}
