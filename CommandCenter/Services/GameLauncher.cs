using System.Diagnostics;
using System.IO;

namespace CommandCenter.Services
{
    public enum Channel { Live, Test }

    // Starts the game the same way the official Generals Online launcher does, then watches for it to close.
    public static class GameLauncher
    {
        public const string TestExe = "GeneralsOnlineZH_TestEnvironment.exe";
        public const string OriginalExe = "generals.exe";

        public static bool HasTestClient => File.Exists(Path.Combine(GamePaths.Game, TestExe));
        public static bool HasOriginalGame => File.Exists(Path.Combine(GamePaths.Game, OriginalExe));

        // Mirrors the official launcher: the live client starts directly; the test client goes through the
        // Easy Anti-Cheat launcher when that anti-cheat is selected in settings.json.
        public static string ExecutableFor(Channel channel)
        {
            bool eac = GoSettings.Load().AntiCheat.Equals("easyanticheat", StringComparison.OrdinalIgnoreCase);
            if (HasTestClient)
                return channel == Channel.Test ? (eac ? GamePaths.EacLauncher : TestExe) : GamePaths.GameExe;
            return eac ? GamePaths.EacLauncher : GamePaths.GameExe;
        }

        public static string Arguments()
        {
            var launcher = LauncherJson.Load();
            return launcher.Windowed ? $"-win -xres {launcher.WindowedWidth} -yres {launcher.WindowedHeight}" : "";
        }

        public static void PlayOnline(Channel channel) => Start(ExecutableFor(channel), Arguments());

        // The original Zero Hour (no Generals Online) for skirmish, campaign and LAN
        public static void PlayOffline() => Start(OriginalExe, "");

        private static void Start(string exe, string arguments)
        {
            var info = new ProcessStartInfo(Path.Combine(GamePaths.Game, exe), arguments)
            {
                WorkingDirectory = GamePaths.Game,
                UseShellExecute = false,
            };
            Process.Start(info)?.Dispose();
        }

        // Looks for the game by process name only; no handle is opened to the game process.
        public static bool IsGameRunning() =>
            Process.GetProcesses().Any(p =>
            {
                string name = p.ProcessName;
                p.Dispose();
                return name.StartsWith("GeneralsOnlineZH", StringComparison.OrdinalIgnoreCase)
                       || name.Equals("generals", StringComparison.OrdinalIgnoreCase)
                       || name.Equals("EAC_LaunchGeneralsOnline", StringComparison.OrdinalIgnoreCase);
            });

        // Waits until the game has started and closed again (or never started within the grace period).
        public static async Task WaitForGameAsync(CancellationToken ct = default)
        {
            var start = DateTime.Now;
            bool seen = false;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                bool running = IsGameRunning();
                if (running)
                    seen = true;
                else if (seen || DateTime.Now - start > TimeSpan.FromSeconds(45))
                    return;
            }
        }
    }
}
