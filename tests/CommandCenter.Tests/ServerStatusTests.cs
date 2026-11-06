namespace CommandCenter.Tests
{
    // Home (ticket 13): the one owner of Generals Online's server numbers. Before the first answer they are being
    // checked; after an ask with no answer the servers are out of reach; one ask runs at a time.
    public sealed class ServerStatusTests
    {
        private TaskCompletionSource<ServerStats?> _answer = new();
        private int _asks;

        private ServerStatus Status() => new(() =>
        {
            _asks++;
            return _answer.Task;
        });

        [Fact]
        public async Task An_answer_brings_the_numbers_and_listeners_hear_it()
        {
            var status = Status();
            int heard = 0;
            status.Changed += () => heard++;
            Assert.Equal((null, false), (status.Stats, status.Asked));

            var ask = status.RefreshAsync();
            _answer.SetResult(new ServerStats(1004, 292, 70));
            await ask;

            Assert.Equal(new ServerStats(1004, 292, 70), status.Stats);
            Assert.True(status.Asked);
            Assert.Equal(1, heard);
        }

        [Fact]
        public async Task No_answer_after_asking_means_the_servers_are_out_of_reach()
        {
            var status = Status();
            _answer.SetResult(new ServerStats(10, 2, 50));
            await status.RefreshAsync();

            _answer = new();
            _answer.SetResult(null);
            await status.RefreshAsync();
            Assert.Equal((null, true), (status.Stats, status.Asked));
        }

        [Fact]
        public async Task Asking_again_while_asking_joins_the_ask_that_runs()
        {
            var status = Status();
            var first = status.RefreshAsync();
            var second = status.RefreshAsync();
            _answer.SetResult(new ServerStats(1, 1, 1));
            await Task.WhenAll(first, second);
            Assert.Equal(1, _asks);
        }
    }
}
