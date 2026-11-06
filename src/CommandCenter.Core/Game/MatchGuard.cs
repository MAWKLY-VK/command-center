using System.IO;
using System.Text.Json;

namespace CommandCenter.Core.Game
{
    // A file that decides whether players match (P5-D3): its path in the game folder, its size and last write time (UTC)
    public sealed record GuardedFile(string Path, long Size, DateTime Written);

    // The guarded files as they were after the last online match that ended without a mismatch, with that match's start
    public sealed record GoodMatch(DateTime Start, IReadOnlyList<GuardedFile> Files);

    // What changed since the last good match. AfterMismatch: the newest match ended in a mismatch, and these changes are
    // its likely reason.
    public sealed record MatchChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed,
        bool AfterMismatch)
    {
        public bool Any => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;
    }

    // The match guard (P5-D3): after each online match without a mismatch it keeps the game's archives and loose INI
    // files by name, size and date, so Command Center can say what changed since then. Generals Online's own files are
    // left out, because its updates reach everyone.
    public static class MatchGuard
    {
        // Thousands of files at about a hundred bytes each; anything larger is not ours
        private const long StoreLimit = 4 * 1024 * 1024;

        private static string StorePath => Path.Combine(GamePaths.AppData, "match-guard.json");

        // Reads the game's last replay: an online match (two or more people) that ended without a mismatch and is newer
        // than the one kept takes its place. Then gives what changed since the kept match, or null before the first one.
        // Called at start and when the game exits.
        public static MatchChanges? Update()
        {
            string game = GamePaths.Game;
            if (game.Length == 0 || !Directory.Exists(game))
                return null;

            var kept = Load();
            var last = ReplayService.Read(ReplayService.LastReplayPath);
            bool newOnlineMatch = last != null && last.Players.Count(p => p.IsHuman) >= 2 && (kept == null || last.Start > kept.Start);
            var now = Files(game);
            if (newOnlineMatch && !last!.Desync)
            {
                kept = new GoodMatch(last.Start, now);
                Save(kept);
            }
            return kept == null ? null : Compare(kept, now, afterMismatch: newOnlineMatch && last!.Desync);
        }

        // The guarded files now: every archive under the game folder and every loose INI file under Data\INI, without
        // Generals Online's files. Data\INI\INIZH.big is a stray copy the game does not load (GameFiles leaves it out too).
        public static List<GuardedFile> Files(string gameFolder)
        {
            var p = GoProfile.Current;
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { p.LiveExe, p.TestExe, p.EacLauncherExe, p.OfficialLauncherExe, p.CommunityPatchFile };
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, MaxRecursionDepth = 16 };
            string ini = Path.Combine(gameFolder, "Data", "INI");
            return Directory.EnumerateFiles(gameFolder, "*.big", options)
                .Concat(Directory.Exists(ini) ? Directory.EnumerateFiles(ini, "*.ini", options) : [])
                .Where(f => !own.Contains(Path.GetFileName(f)))
                .Select(f => new FileInfo(f))
                .Select(f => new GuardedFile(Path.GetRelativePath(gameFolder, f.FullName), f.Length, f.LastWriteTimeUtc))
                .Where(f => !f.Path.Equals(@"Data\INI\INIZH.big", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static MatchChanges Compare(GoodMatch kept, IReadOnlyList<GuardedFile> now, bool afterMismatch)
        {
            var before = kept.Files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var after = now.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            List<string> Sorted(IEnumerable<string> paths) => paths.Order(StringComparer.OrdinalIgnoreCase).ToList();
            return new MatchChanges(
                Sorted(after.Keys.Where(path => !before.ContainsKey(path))),
                Sorted(before.Keys.Where(path => !after.ContainsKey(path))),
                Sorted(now.Where(f => before.TryGetValue(f.Path, out var old) && (old.Size != f.Size || old.Written != f.Written)).Select(f => f.Path)),
                afterMismatch);
        }

        // A missing or damaged record counts as none; the next good match writes a new one
        private static GoodMatch? Load()
        {
            try
            {
                return File.Exists(StorePath) && JsonSerializer.Deserialize<GoodMatch>(BoundedFile.Read(StorePath, StoreLimit)) is { Files: not null } kept
                    ? kept
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static void Save(GoodMatch kept)
        {
            try
            {
                Directory.CreateDirectory(GamePaths.AppData);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(kept));
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
            }
        }
    }
}
