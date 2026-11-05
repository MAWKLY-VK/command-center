using System.Diagnostics;
using System.Reflection;

// The language is a setting for the whole program (Loc), so tests that switch it must not run at the same time as others
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CommandCenter.Tests
{
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
        public string Write(string relative, string text) => Write(relative, System.Text.Encoding.UTF8.GetBytes(text));

        public string Write(string relative, byte[] content)
        {
            string path = System.IO.Path.Combine(Root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
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

    // A game archive (.big) holding these files, in the format GameFiles reads: "BIGF", the archive size
    // (little-endian), the file count and the offset of the first file (big-endian), then each file's offset and size
    // (big-endian) and zero-terminated name, then the files themselves
    internal static class BigArchives
    {
        public static void Write(string path, params (string Name, byte[] Data)[] files)
        {
            int indexSize = 16 + files.Sum(f => 8 + f.Name.Length + 1);
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write(System.Text.Encoding.ASCII.GetBytes("BIGF"));
            w.Write(indexSize + files.Sum(f => f.Data.Length));
            BigEndian(w, files.Length);
            BigEndian(w, indexSize);
            int offset = indexSize;
            foreach (var (name, data) in files)
            {
                BigEndian(w, offset);
                BigEndian(w, data.Length);
                w.Write(System.Text.Encoding.ASCII.GetBytes(name));
                w.Write((byte)0);
                offset += data.Length;
            }
            foreach (var (_, data) in files)
                w.Write(data);
            w.Flush();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, stream.ToArray());
        }

        private static void BigEndian(BinaryWriter w, int value) =>
            w.Write(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
    }

    // Runs a test in Arabic and puts English back afterwards
    internal sealed class Arabic : IDisposable
    {
        public Arabic() => Loc.Init("ar");

        public void Dispose() => Loc.Init("en");
    }
}
