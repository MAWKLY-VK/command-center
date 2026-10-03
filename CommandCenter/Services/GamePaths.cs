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

        // The game folder: --game, then the folder chosen in Command Center, then the working folder and the folder of
        // this program, then the Steam libraries. A Zero Hour folder with Generals Online comes first, one without it
        // next (Generals Online not installed). No other folder is ever taken for the game, because fixes and add-ons
        // change files inside it: when nothing is found it stays empty and ZeroHourFound is false.
        public static string Game
        {
            get => _game ??= Detect() ?? "";
            set => _game = value;
        }

        // Zero Hour is there, with Generals Online
        public static bool GameFound => ZeroHourFound && File.Exists(Path.Combine(Game, GameExe));

        // Zero Hour is there, with or without Generals Online
        public static bool ZeroHourFound => Game.Length > 0 && IsZeroHour(Game);

        // Zero Hour's own archives, present in every installation with or without Generals Online
        public static bool IsZeroHour(string folder)
        {
            try
            {
                return Path.IsPathFullyQualified(folder)
                       && File.Exists(Path.Combine(folder, "WindowZH.big")) && File.Exists(Path.Combine(folder, "INIZH.big"));
            }
            catch
            {
                return false;
            }
        }

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
            var candidates = Candidates().Where(IsZeroHour).ToList();
            return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, GameExe))) ?? candidates.FirstOrDefault();
        }

        private static IEnumerable<string> Candidates()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.FindIndex(args, a => a.Equals("--game", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < args.Length)
                yield return Full(args[index + 1]);
            if (AppSettings.Current.GameFolder is { Length: > 0 } chosen)
                yield return chosen;
            yield return Directory.GetCurrentDirectory();
            yield return AppContext.BaseDirectory.TrimEnd('\\');
            foreach (string library in SteamLibraries())
                yield return Path.Combine(library, "steamapps", "common", "Command & Conquer Generals - Zero Hour");
        }

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return ""; }
        }

        // Remembers a folder the player picked; false when it is not a Zero Hour folder
        public static bool UseGameFolder(string folder)
        {
            if (!IsZeroHour(folder))
                return false;
            _game = folder;
            AppSettings.Current.GameFolder = folder;
            AppSettings.Current.Save();
            return true;
        }

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
