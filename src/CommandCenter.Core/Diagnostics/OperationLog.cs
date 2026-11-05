using System.IO;
using System.Text;
using System.Text.Json;

namespace CommandCenter.Core.Diagnostics
{
    // A record of every change Command Center makes, and of every attempt that failed with its reason (phase 1 decision
    // D3). One JSON line each, in operations.log in Command Center's data folder, on this PC only:
    //     {"time":"2026-11-05T14:03:12.4+03:00","operation":"write","target":"C:\\...\\Options.ini","result":"done","detail":"..."}
    // Past 1 MB the file becomes operations.1.log, replacing the one before. Writing the log never throws, so a problem
    // with the log cannot stop the change it describes. It complements Backups\backups.json, which lists only what can
    // still be undone.
    public static class OperationLog
    {
        public const long MaxBytes = 1024 * 1024;

        public static string FilePath => Path.Combine(GamePaths.AppData, "operations.log");
        public static string PreviousPath => Path.Combine(GamePaths.AppData, "operations.1.log");

        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static void Done(string operation, string target, string detail = "") => Write(operation, target, "done", detail);

        public static void Failed(string operation, string target, string reason) => Write(operation, target, "failed", reason);

        // Runs a change and records how it went. An error is recorded with its message and still reaches the caller.
        public static T Record<T>(string operation, string target, string detail, Func<T> change)
        {
            try
            {
                T result = change();
                Done(operation, target, detail);
                return result;
            }
            catch (Exception error)
            {
                Failed(operation, target, Reason(detail, error));
                throw;
            }
        }

        public static void Record(string operation, string target, string detail, Action change) =>
            Record(operation, target, detail, () =>
            {
                change();
                return true;
            });

        public static async Task<T> RecordAsync<T>(string operation, string target, string detail, Func<Task<T>> change)
        {
            try
            {
                T result = await change().ConfigureAwait(false);
                Done(operation, target, detail);
                return result;
            }
            catch (Exception error)
            {
                Failed(operation, target, Reason(detail, error));
                throw;
            }
        }

        // "Title · detail", or the title alone
        public static string Describe(string title, string detail) => detail.Length > 0 ? $"{title} · {detail}" : title;

        private static string Reason(string detail, Exception error) => detail.Length > 0 ? $"{detail}: {error.Message}" : error.Message;

        private static void Write(string operation, string target, string result, string detail)
        {
            try
            {
                string line = JsonSerializer.Serialize(new Line(DateTimeOffset.Now, operation, target, result, detail), Json);
                lock (Gate)
                {
                    Directory.CreateDirectory(GamePaths.AppData);
                    var current = new FileInfo(FilePath);
                    if (current.Exists && current.Length + Encoding.UTF8.GetByteCount(line) + 2 > MaxBytes)
                        File.Move(FilePath, PreviousPath, overwrite: true);
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // The log must never stop the change it describes
            }
        }

        private sealed record Line(DateTimeOffset Time, string Operation, string Target, string Result, string Detail);
    }
}
