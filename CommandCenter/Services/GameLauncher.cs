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

        // How long after Play the game is helped to the front if the focus falls back to this program
        private static readonly TimeSpan LaunchPhase = TimeSpan.FromSeconds(90);

        // Waits until the game has started and closed again (or never started within the grace period).
        //
        // A full-screen game drops to the taskbar as soon as another window takes the focus. During the launch the
        // anti-cheat splash closes and the game swaps its own splash for the main window; at those moments Windows
        // gives the focus to the window underneath, which is this program as long as it stays open (so it does,
        // like the official launcher). When that happens the game is put back in front at once, the same as a click
        // on its taskbar button. settled runs once, after the launch, when the game holds the foreground.
        public static async Task WaitForGameAsync(IntPtr own, Action? settled = null, CancellationToken ct = default)
        {
            var log = new PlayLog();
            log.Add("Play pressed");
            var start = DateTime.Now;
            bool seen = false, done = false, wasLaunch = true;
            int missing = 0;
            string lastFront = "", lastGame = "";
            DateTime? frontSince = null;
            while (!ct.IsCancellationRequested)
            {
                bool launching = DateTime.Now - start < LaunchPhase;
                await Task.Delay(launching ? 250 : 1000, ct);

                var names = ProcessNames();
                var ids = names.Where(p => IsGameProcess(p.Value)).Select(p => p.Key).ToHashSet();
                if (ids.Count == 0)
                {
                    // The anti-cheat launcher hands over to the game, so allow a short gap before calling it closed
                    if (seen ? ++missing >= (launching ? 12 : 3) : DateTime.Now - start > TimeSpan.FromSeconds(45))
                    {
                        log.Add(seen ? "Game closed" : "The game did not start");
                        return;
                    }
                    continue;
                }
                seen = true;
                missing = 0;

                IntPtr front = GetForegroundWindow();
                GetWindowThreadProcessId(front, out uint frontPid);
                string frontName = front == IntPtr.Zero ? "(nothing)" : names.GetValueOrDefault((int)frontPid, "?");
                if (frontName != lastFront)
                    log.Add($"In front: {frontName}");
                lastFront = frontName;

                IntPtr game = GameWindow(ids, names);
                string gameState = game == IntPtr.Zero ? "no window yet" : IsIconic(game) ? "on the taskbar" : "shown";
                if (gameState != lastGame)
                    log.Add($"Game window: {gameState}");
                lastGame = gameState;

                // The focus fell to this program or to nothing, or it just went from the game to another program
                // (Windows still lets this program act on the Play click while no other input came since)
                bool frontIsLaunch = front == own || front == IntPtr.Zero || ids.Contains((int)frontPid);
                bool justLeft = !frontIsLaunch && wasLaunch;
                wasLaunch = frontIsLaunch;
                if (launching && game != IntPtr.Zero && front != game && (front == own || front == IntPtr.Zero || justLeft))
                {
                    bool ok = BringToFront(game);
                    log.Add(ok ? "Put the game back in front" : "Could not put the game back in front");
                }

                frontSince = ids.Contains((int)frontPid) ? frontSince ?? DateTime.Now : null;
                if (!done && !launching && frontSince is { } since && DateTime.Now - since > TimeSpan.FromSeconds(3))
                {
                    done = true;
                    settled?.Invoke();
                }
            }
        }

        // Process names by id from one list of the running processes (no handle is opened to any of them)
        private static Dictionary<int, string> ProcessNames()
        {
            var names = new Dictionary<int, string>();
            foreach (var p in Process.GetProcesses())
            {
                names[p.Id] = p.ProcessName;
                p.Dispose();
            }
            return names;
        }

        // The game's own window: the largest visible top-level window of the game, not of its anti-cheat launcher.
        // A window on the taskbar counts too, so it can be brought back.
        private static IntPtr GameWindow(HashSet<int> ids, Dictionary<int, string> names)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window))
                    return true;
                GetWindowThreadProcessId(window, out uint pid);
                if (!ids.Contains((int)pid) || names.GetValueOrDefault((int)pid, "").StartsWith("EAC_", StringComparison.OrdinalIgnoreCase))
                    return true;
                long area = 0;
                if (!IsIconic(window) && GetWindowRect(window, out var rect))
                    area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    best = window;
                    bestArea = area;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // What a click on the game's taskbar button does. Windows allows it while this program holds the foreground.
        private static bool BringToFront(IntPtr window)
        {
            if (IsIconic(window))
                ShowWindow(window, Restore);
            return SetForegroundWindow(window);
        }

        // A short record of the last launch (what held the foreground, and when), for when the game still drops out
        private sealed class PlayLog
        {
            private readonly DateTime _start = DateTime.Now;
            private readonly System.Text.StringBuilder _text = new();

            public void Add(string line)
            {
                _text.Append($"{(DateTime.Now - _start).TotalSeconds,6:0.0}s  {line}\r\n");
                try
                {
                    Directory.CreateDirectory(GamePaths.AppData);
                    File.WriteAllText(Path.Combine(GamePaths.AppData, "play.log"), _text.ToString());
                }
                catch { }
            }
        }

        private const int Restore = 9;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        private const int AnyProcess = -1;

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
