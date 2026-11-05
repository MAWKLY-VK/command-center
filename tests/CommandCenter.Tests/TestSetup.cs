using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

// The language is a setting for the whole program (Loc), so tests that switch it must not run at the same time as others
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CommandCenter.Tests
{
    internal static class TestStartup
    {
        // The services get their WPF pieces from the App at startup (AppServices); the tests need them as well
        [ModuleInitializer]
        internal static void ConnectWpf() => AppServices.ConnectWpf();
    }

    // A file in the temporary folder that is deleted when the test ends
    internal sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N"));

        public TempFile(byte[] content) => File.WriteAllBytes(Path, content);

        public TempFile(string content) => File.WriteAllText(Path, content);

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }

    // A game folder and a user data folder in the temporary folder, used as Command Center's for one test. The values
    // before are taken from GamePaths' fields and put back afterwards, so the real game is never looked up.
    internal sealed class TempGame : IDisposable
    {
        private static readonly FieldInfo GameField = typeof(GamePaths).GetField("_game", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo UserDataField = typeof(GamePaths).GetField("_userData", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object? _game = GameField.GetValue(null);
        private readonly object? _userData = UserDataField.GetValue(null);

        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N"));
        public string Game => System.IO.Path.Combine(Root, "game");
        public string UserData => System.IO.Path.Combine(Root, "user");

        public TempGame()
        {
            Directory.CreateDirectory(Game);
            Directory.CreateDirectory(UserData);
            File.WriteAllBytes(System.IO.Path.Combine(Game, "WindowZH.big"), []);
            File.WriteAllBytes(System.IO.Path.Combine(Game, "INIZH.big"), []);
            GamePaths.Game = Game;
            GamePaths.UserData = UserData;
        }

        // A file under the root (game, user or anywhere else in it) with this text; returns its full path
        public string Write(string relative, string text)
        {
            string path = System.IO.Path.Combine(Root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            GameField.SetValue(null, _game);
            UserDataField.SetValue(null, _userData);
            try
            {
                foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
            catch { }
        }
    }

    // Makes the test host itself count as the running game: a GO profile names its process as the live client
    internal sealed class GameRunning : IDisposable
    {
        private readonly string _file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N") + ".json");

        public GameRunning()
        {
            string exe = Process.GetCurrentProcess().ProcessName + ".exe";
            File.WriteAllText(_file, $$"""
                {
                  "schema": 1,
                  "liveExe": "{{exe}}",
                  "testExe": "GeneralsOnlineZH_TestEnvironment.exe",
                  "eacLauncherExe": "EAC_LaunchGeneralsOnline.exe",
                  "officialLauncherExe": "GeneralsOnlineZH.exe",
                  "eacPlugin": "easyanticheat",
                  "channelKey": "prefer_experiemental_client",
                  "communityPatchFile": "500_900_CommunityPatch_CoreINI.big",
                  "apiBase": "https://api.playgenerals.online/env/prod/contract/1/",
                  "manifestKey": "execrc_60"
                }
                """);
            GoProfile.FileOverride = _file;
            GoProfile.Reset();
        }

        public void Dispose()
        {
            GoProfile.FileOverride = null;
            GoProfile.Reset();
            try { File.Delete(_file); } catch { }
        }
    }

    // A game text table (generals.csf) written byte by byte without CsfFile, so a table that CsfFile produces can be
    // compared with one made independently: the header, then each label with one string, its UTF-16 text inverted
    internal static class CsfTables
    {
        public static byte[] Of(params (string Label, string Text)[] labels)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write(System.Text.Encoding.ASCII.GetBytes(" FSC"));
            w.Write(3);
            w.Write(labels.Length);
            w.Write(labels.Length);
            w.Write(0);
            w.Write(0);
            foreach (var (label, text) in labels)
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes(" LBL"));
                w.Write(1);
                w.Write(label.Length);
                w.Write(System.Text.Encoding.ASCII.GetBytes(label));
                w.Write(System.Text.Encoding.ASCII.GetBytes(" RTS"));
                w.Write(text.Length);
                w.Write(System.Text.Encoding.Unicode.GetBytes(text).Select(b => (byte)~b).ToArray());
            }
            w.Flush();
            return stream.ToArray();
        }
    }

    // Runs a test in Arabic and puts English back afterwards
    internal sealed class Arabic : IDisposable
    {
        public Arabic() => Loc.Init("ar");

        public void Dispose() => Loc.Init("en");
    }
}
