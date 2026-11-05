using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CommandCenter.Core.Safety
{
    // Where Command Center may change files: the game folder and the game's Documents folder, never Windows. Paths are
    // judged by where they really lead, with every junction and symbolic link on the way followed, so a link inside an
    // allowed folder cannot take a change outside it (spec phase 3).
    public static class PathGuard
    {
        public static bool IsAllowed(string path)
        {
            try
            {
                string real = RealPath(path);
                var (windows, game, userData) = Roots();
                return !IsInside(real, windows) && (IsInside(real, game) || IsInside(real, userData));
            }
            catch
            {
                // A path that cannot be resolved is not one Command Center changes
                return false;
            }
        }

        // The real Windows, game and user data folders ("" when there is none), resolved again only when the folders
        // Command Center uses change, so a batch of files does not resolve them for every file
        private static (string Key, string Windows, string Game, string UserData)? _roots;

        private static (string Windows, string Game, string UserData) Roots()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string game = GamePaths.ZeroHourFound ? GamePaths.Game : "";
            string userData = GamePaths.UserData;
            string key = windows + "|" + game + "|" + userData;
            if (_roots is not { } roots || roots.Key != key)
            {
                roots = (key, RealPath(windows), game.Length > 0 ? RealPath(game) : "", userData.Length > 0 ? RealPath(userData) : "");
                _roots = roots;
            }
            return (roots.Windows, roots.Game, roots.UserData);
        }

        // Whether path really lies inside folder
        public static bool IsWithin(string path, string folder)
        {
            try
            {
                return IsInside(RealPath(path), RealPath(folder));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInside(string path, string root) =>
            root.Length > 0 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        // The full path, with the links on the part that exists resolved by Windows; the part that does not exist yet
        // follows as it is
        public static string RealPath(string path)
        {
            string existing = Path.GetFullPath(path);
            var missing = new Stack<string>();
            while (!File.Exists(existing) && !Directory.Exists(existing))
            {
                string? parent = Path.GetDirectoryName(existing);
                if (parent == null)
                    throw new DirectoryNotFoundException($"No part of {path} exists.");
                missing.Push(Path.GetFileName(existing));
                existing = parent;
            }
            return Path.Combine([FinalPath(existing), .. missing]);
        }

        private static string FinalPath(string existing)
        {
            // No access is asked for, so a file in use can be resolved too; directories need backup semantics to open
            using var handle = CreateFile(existing, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = new char[512];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length >= buffer.Length)
            {
                buffer = new char[length];
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            }
            if (length == 0 || length >= buffer.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            string final = new(buffer, 0, (int)length);
            if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + final[8..];
            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }

        private const uint BackupSemantics = 0x02000000;

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
    }
}
