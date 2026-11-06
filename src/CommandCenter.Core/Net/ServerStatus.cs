namespace CommandCenter.Core.Net
{
    // Generals Online's server numbers for Home's data line and PLAY's "Ready" line (ticket 13)
    public interface IServerStatus
    {
        // The last answer; null before the first one, or when the last ask had no answer
        ServerStats? Stats { get; }

        // An answer was asked for at least once, so a missing one means the servers are out of reach
        bool Asked { get; }

        event Action? Changed;

        // Asks again; asked while an ask runs, it joins that one
        Task RefreshAsync();
    }

    // The one owner of the server numbers. fetch asks the servers and gives null when they do not answer.
    public sealed class ServerStatus(Func<Task<ServerStats?>> fetch) : IServerStatus
    {
        private Task _running = Task.CompletedTask;
        private bool _asking;

        public ServerStats? Stats { get; private set; }
        public bool Asked { get; private set; }
        public event Action? Changed;

        // A flag, not the task itself, marks an ask in progress: an answer that is there at once ends the ask before
        // its task could be stored
        public Task RefreshAsync()
        {
            if (_asking)
                return _running;
            _asking = true;
            _running = AskAsync();
            return _running;
        }

        private async Task AskAsync()
        {
            try
            {
                Stats = await fetch();
                Asked = true;
            }
            finally
            {
                _asking = false;
            }
            Changed?.Invoke();
        }
    }
}
