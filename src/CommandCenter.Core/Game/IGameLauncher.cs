namespace CommandCenter.Core.Game
{
    // Play's side of Windows (ticket 13): which programs are there, starting the game or the official launcher, and
    // watching the game until it closes. The play flow reaches Windows only through this, so its every step can be
    // tested with a stand-in.
    public interface IGameLauncher
    {
        bool HasTestClient { get; }
        bool HasOfficialLauncher { get; }
        bool IsRunning();

        // The program for the channel is in the game folder
        bool CanStart(Channel channel);
        string ExecutableFor(Channel channel);

        void Start(Channel channel);
        void StartOfficialLauncher();

        // Until the game has started and closed again; settled runs once, after the launch, when the game holds the
        // foreground. False when the game did not start in time.
        Task<bool> WaitAsync(IntPtr window, Action settled);
    }

    // The real one: GameLauncher's rules and its Windows process and window handling
    public sealed class WindowsGameLauncher : IGameLauncher
    {
        public bool HasTestClient => GameLauncher.HasTestClient;
        public bool HasOfficialLauncher => GameLauncher.HasOfficialLauncher;
        public bool IsRunning() => GameLauncher.IsGameRunning();
        public bool CanStart(Channel channel) => GameLauncher.CanStart(channel);
        public string ExecutableFor(Channel channel) => GameLauncher.ExecutableFor(channel);
        public void Start(Channel channel) => GameLauncher.PlayOnline(channel);
        public void StartOfficialLauncher() => GameLauncher.StartOfficialLauncher();
        public Task<bool> WaitAsync(IntPtr window, Action settled) => GameLauncher.WaitForGameAsync(window, settled);
    }
}
