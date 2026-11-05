using System.IO;

namespace CommandCenter.Core.Diagnostics
{
    // errors.log in Command Center's data folder, to attach to a bug report: errors nobody caught, and the damaged files
    // Command Center skipped. It starts over past 256 KB. Writing to it never fails the caller.
    public static class ErrorLog
    {
        private const long Limit = 256 * 1024;
        private static readonly object Gate = new();

        // A damaged file is written down once in a run, however often it is read
        private static readonly HashSet<string> SkippedThisRun = new(StringComparer.OrdinalIgnoreCase);

        public static string FilePath => Path.Combine(GamePaths.AppData, "errors.log");

        public static void Write(Exception error) => Append(error + Environment.NewLine);

        // One line: which file was skipped and why
        public static void Skipped(string file, string reason)
        {
            lock (Gate)
            {
                if (!SkippedThisRun.Add(file))
                    return;
            }
            Append($"Skipped {file}: {reason}");
        }

        private static void Append(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(GamePaths.AppData);
                    string path = FilePath;
                    if (File.Exists(path) && new FileInfo(path).Length > Limit)
                        File.Delete(path);
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {UpdateService.Current.ToString(2)}  {text}{Environment.NewLine}");
                }
            }
            catch
            {
                // The log is a help, never a reason to fail
            }
        }
    }
}
