using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CommandCenter.Core.Diagnostics
{
    // One Command Center per data folder (phase 1 decision D2), so two copies never write the same settings, backups or
    // Undo list at the same time. Copies with another data folder (tests, --capture) run side by side.
    //  - The lock is a mutex named after the full data folder path (letter case ignored), in this Windows session.
    //  - The first copy keeps its process id in a small named memory block and listens for a named event.
    //  - A later copy lets that process take the foreground (AllowSetForegroundWindow, which takes one process id), sets
    //    the event and closes; the first copy then shows its window.
    //  - A copy that Command Center started itself (after an update or a language switch) is given the old copy's process
    //    id with --restart-of, and waits for it to exit before taking the lock.
    // The lock belongs to the thread that took it, so Dispose must run on that thread (the App's UI thread).
    public sealed class SingleInstance : IDisposable
    {
        public const string RestartOption = "--restart-of";

        private readonly string _name;
        private readonly Mutex _lock;
        private readonly EventWaitHandle _showRequests;
        private readonly MemoryMappedFile? _processId;
        private RegisteredWaitHandle? _listening;

        public bool IsFirst { get; }

        private SingleInstance(string name, Mutex @lock, EventWaitHandle showRequests, MemoryMappedFile? processId, bool isFirst)
        {
            _name = name;
            _lock = @lock;
            _showRequests = showRequests;
            _processId = processId;
            IsFirst = isFirst;
        }

        public static SingleInstance Acquire(string dataFolder, int? restartOf = null)
        {
            if (restartOf is int old)
                WaitForExit(old, TimeSpan.FromSeconds(30));

            string name = NameFor(dataFolder);
            var mutex = new Mutex(initiallyOwned: false, $@"Local\{name}");
            bool first;
            try
            {
                first = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The copy before ended without letting go (it crashed or was killed): the lock is ours now
                first = true;
            }

            var showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}-show");
            MemoryMappedFile? processId = null;
            if (first)
            {
                processId = MemoryMappedFile.CreateOrOpen($@"Local\{name}-process", sizeof(int));
                using var view = processId.CreateViewAccessor();
                view.Write(0, Environment.ProcessId);
            }
            return new SingleInstance(name, mutex, showRequests, processId, first);
        }

        // The first copy: calls show, on a pool thread, each time a later copy asks for the window
        public void OnShowRequested(Action show)
        {
            if (!IsFirst)
                throw new InvalidOperationException("Only the first copy shows its window on request.");
            _listening?.Unregister(null);
            _listening = ThreadPool.RegisterWaitForSingleObject(_showRequests, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        // A later copy: lets the first copy take the foreground, then asks it to show its window
        public void AskFirstToShow()
        {
            if (FirstProcessId() is int first)
                AllowSetForegroundWindow(first);
            _showRequests.Set();
        }

        private int? FirstProcessId()
        {
            try
            {
                using var map = MemoryMappedFile.OpenExisting($@"Local\{_name}-process", MemoryMappedFileRights.Read);
                using var view = map.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.Read);
                return view.ReadInt32(0);
            }
            catch
            {
                return null;
            }
        }

        public static string NameFor(string dataFolder)
        {
            string key = Path.GetFullPath(dataFolder).TrimEnd('\\').ToUpperInvariant();
            return "CommandCenter-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        }

        // The arguments for a copy Command Center starts itself: the current ones without an earlier restart marker, plus
        // this process's id, so the new copy waits for this one to exit
        public static List<string> ArgumentsForRestart(IEnumerable<string> current)
        {
            var result = new List<string>();
            bool skipValue = false;
            foreach (string arg in current)
            {
                if (skipValue)
                {
                    skipValue = false;
                    continue;
                }
                if (arg.Equals(RestartOption, StringComparison.OrdinalIgnoreCase))
                {
                    skipValue = true;
                    continue;
                }
                result.Add(arg);
            }
            result.Add(RestartOption);
            result.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            return result;
        }

        // The process id after --restart-of, or null
        public static int? RestartOf(IReadOnlyList<string> args)
        {
            for (int i = 0; i + 1 < args.Count; i++)
            {
                if (args[i].Equals(RestartOption, StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null;
            }
            return null;
        }

        private static void WaitForExit(int processId, TimeSpan timeout)
        {
            try
            {
                using var old = Process.GetProcessById(processId);
                old.WaitForExit(timeout);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Already gone
            }
        }

        public void Dispose()
        {
            _listening?.Unregister(null);
            if (IsFirst)
            {
                try
                {
                    _lock.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not on the thread that took it: Windows releases the lock when the process ends
                }
            }
            _lock.Dispose();
            _showRequests.Dispose();
            _processId?.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
