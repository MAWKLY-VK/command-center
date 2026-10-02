using System.IO;
using System.Windows.Threading;
using CommandCenter.Services;

namespace CommandCenter.Pages.Tools
{
    // Sends maps or replays to the Recycle Bin, where Windows can restore them to where they were, and keeps a copy
    // for as long as the message offers Undo, so Undo puts them back at once. The copy is removed afterwards.
    internal static class RecycleUndo
    {
        private static readonly TimeSpan UndoTime = TimeSpan.FromSeconds(15);

        private static string Root => Path.Combine(GamePaths.AppData, "Undo");

        // Returns the Undo action for what was removed; failed lists what could not be removed, with the reason
        public static Action Recycle(IReadOnlyList<string> paths, out List<string> removed, out List<string> failed)
        {
            CleanUp();
            string hold = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            var done = new List<(string Original, string Copy)>();
            removed = new List<string>();
            failed = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i].TrimEnd(Path.DirectorySeparatorChar);
                string copy = Path.Combine(hold, i.ToString(), Path.GetFileName(path));
                try
                {
                    if (Directory.Exists(path))
                    {
                        CopyFolder(path, copy);
                        RecycleBin.SendFolder(path);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                        File.Copy(path, copy);
                        RecycleBin.SendFile(path);
                    }
                    if (Directory.Exists(path) || File.Exists(path))
                        throw new IOException(Loc.T("It is still there."));
                    done.Add((path, copy));
                    removed.Add(path);
                }
                catch (Exception ex)
                {
                    failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            var timer = new DispatcherTimer { Interval = UndoTime };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                TryDeleteFolder(hold);
            };
            if (done.Count > 0)
                timer.Start();
            else
                TryDeleteFolder(hold);

            return () =>
            {
                timer.Stop();
                foreach (var (original, copy) in done)
                {
                    if (Directory.Exists(original) || File.Exists(original))
                        continue;
                    if (Directory.Exists(copy))
                        Directory.Move(copy, original);
                    else if (File.Exists(copy))
                        File.Move(copy, original);
                }
                TryDeleteFolder(hold);
            };
        }

        // Copies left behind when the program closed while Undo was still offered
        public static void CleanUp()
        {
            try
            {
                if (!Directory.Exists(Root))
                    return;
                foreach (var folder in new DirectoryInfo(Root).EnumerateDirectories())
                {
                    if (DateTime.Now - folder.CreationTime > UndoTime + TimeSpan.FromMinutes(1))
                        TryDeleteFolder(folder.FullName);
                }
            }
            catch { }
        }

        private static void CopyFolder(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            foreach (string folder in Directory.EnumerateDirectories(source))
                CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
        }

        private static void TryDeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch { }
        }
    }
}
