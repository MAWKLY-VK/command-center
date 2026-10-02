using System.IO;
using Microsoft.Win32;

namespace CommandCenter.Services
{
    // Where the game, its user data and Generals Online's own data live.
    public static class GamePaths
    {
        public const string SteamAppId = "2732960";
        public const string GameExe = "GeneralsOnlineZH_60.exe";
        public const string EacLauncher = "EAC_LaunchGeneralsOnline.exe";

        private static string? _game;

        // The game folder: --game argument, then the working folder, then the folder of this program,
        // then the Steam library that holds Zero Hour.
        public static string Game
        {
            get
            {
                if (_game != null)
                    return _game;
                _game = Detect() ?? Directory.GetCurrentDirectory();
                return _game;
            }
            set => _game = value;
        }

        public static bool GameFound => File.Exists(Path.Combine(Game, GameExe));

        private static string? _userData;

        // The game's folder in Documents; --user-data <folder> points it elsewhere for tests
        public static string UserData
        {
            get
            {
                if (_userData != null)
                    return _userData;
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.FindIndex(args, a => a.Equals("--user-data", StringComparison.OrdinalIgnoreCase));
                _userData = index >= 0 && index + 1 < args.Length
                    ? Path.GetFullPath(args[index + 1])
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Command and Conquer Generals Zero Hour Data");
                return _userData;
            }
            set => _userData = value;
        }

        public static string GoData => Path.Combine(UserData, "GeneralsOnlineData");
        public static string Options => Path.Combine(UserData, "Options.ini");
        public static string GoSettings => Path.Combine(GoData, "settings.json");
        public static string LauncherSettings => Path.Combine(GoData, "launcher.json");
        public static string Replays => Path.Combine(UserData, "Replays");
        public static string ArchivedReplays => Path.Combine(UserData, "ArchivedReplays");
        public static string Maps => Path.Combine(UserData, "Maps");

        // Our own files stay in a folder of their own so nothing of Generals Online is touched
        public static string AppData => Path.Combine(UserData, "CommandCenter");

        private static string? Detect()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.FindIndex(args, a => a.Equals("--game", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < args.Length && IsGame(args[index + 1]))
                return args[index + 1];

            foreach (string candidate in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                if (IsGame(candidate))
                    return candidate;
            }

            foreach (string library in SteamLibraries())
            {
                string path = Path.Combine(library, "steamapps", "common", "Command & Conquer Generals - Zero Hour");
                if (IsGame(path))
                    return path;
            }
            return null;
        }

        private static bool IsGame(string folder) => File.Exists(Path.Combine(folder, GameExe));

        public static string? SteamFolder()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
                    return path.Replace('/', '\\');
            }
            catch { }
            string fallback = @"C:\Program Files (x86)\Steam";
            return Directory.Exists(fallback) ? fallback : null;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            string? steam = SteamFolder();
            if (steam == null)
                yield break;
            yield return steam;

            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                yield break;
            foreach (string line in File.ReadLines(vdf))
            {
                string t = line.Trim();
                if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    yield return parts[^1].Replace(@"\\", @"\");
            }
        }

        // Steam's own artwork for Zero Hour (hero image and logo), used only as a backdrop
        public static string? SteamArt(string name)
        {
            string? steam = SteamFolder();
            if (steam == null)
                return null;
            string path = Path.Combine(steam, "appcache", "librarycache", SteamAppId, name);
            return File.Exists(path) ? path : null;
        }
    }
}
