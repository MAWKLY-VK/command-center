using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CommandCenter.Services
{
    public enum Channel { Live, Test }

    // Starts the game the same way the official Generals Online launcher does, then watches for it to close.
    public static class GameLauncher
    {
        public const string TestExe = "GeneralsOnlineZH_TestEnvironment.exe";

        public static bool HasTestClient => File.Exists(Path.Combine(GamePaths.Game, TestExe));

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

        private static void Start(string exe, string arguments)
        {
            // The game window comes from a process started by the anti-cheat launcher, not by us. Windows only lets it
            // take the foreground if we pass that right on while we still hold it; without this the full-screen game
            // opens behind and drops to the taskbar. The official launcher does the same.
            AllowSetForegroundWindow(AnyProcess);

            var info = new ProcessStartInfo(Path.Combine(GamePaths.Game, exe), arguments)
            {
                WorkingDirectory = GamePaths.Game,
                UseShellExecute = false,
            };
            Process.Start(info)?.Dispose();
        }

        // The game, its anti-cheat launcher and the game's own updater. GeneralsOnlineZH.exe on its own is the official
        // launcher, which stays open next to the game, so it does not count.
        private static bool IsGameProcess(string name) =>
            (name.StartsWith("GeneralsOnlineZH_", StringComparison.OrdinalIgnoreCase)
             || name.StartsWith("GeneralsOnline_update", StringComparison.OrdinalIgnoreCase)
             || name.Equals("generals", StringComparison.OrdinalIgnoreCase)
             || name.Equals("EAC_LaunchGeneralsOnline", StringComparison.OrdinalIgnoreCase));

        // Ids of the running game processes, by name only; no handle is opened to the game process
        private static HashSet<int> GameProcessIds()
        {
            var ids = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                if (IsGameProcess(p.ProcessName))
                    ids.Add(p.Id);
                p.Dispose();
            }
            return ids;
        }

        public static bool IsGameRunning() => GameProcessIds().Count > 0;

        public static List<string> RunningGames()
        {
            var names = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                if (IsGameProcess(p.ProcessName) && !names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    names.Add(p.ProcessName);
                p.Dispose();
            }
            return names;
        }

        // True when the window in front belongs to the game (or its anti-cheat splash)
        public static bool GameIsInFront()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(window, out uint pid);
            return GameProcessIds().Contains((int)pid);
        }

        // Waits until the game has started and closed again (or never started within the grace period).
        // inFront runs once, when the game first holds the foreground.
        public static async Task WaitForGameAsync(Action? inFront = null, CancellationToken ct = default)
        {
            var start = DateTime.Now;
            bool seen = false, shown = false;
            int missing = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (IsGameRunning())
                {
                    seen = true;
                    missing = 0;
                    if (!shown && inFront != null && GameIsInFront())
                    {
                        shown = true;
                        inFront();
                    }
                }
                // The anti-cheat launcher hands over to the game, so allow a short gap before calling it closed
                else if (seen ? ++missing >= 3 : DateTime.Now - start > TimeSpan.FromSeconds(45))
                    return;
            }
        }

        private const int AnyProcess = -1;

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
