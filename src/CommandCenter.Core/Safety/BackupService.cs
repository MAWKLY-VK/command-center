using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CommandCenter.Core.Safety
{
    public sealed class BackupEntry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Kind { get; set; } = "file"; // file, move, registry or readonly
        public string Target { get; set; } = "";
        public string? BackupFile { get; set; }
        public string AfterHash { get; set; } = "";
        public DateTime When { get; set; }

        // registry: Target is the key under HKEY_CURRENT_USER, BackupFile holds the value name
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }

        // readonly: Target is the folder, Files the files whose read-only flag was cleared
        public List<string>? Files { get; set; }
    }

    // Every file the launcher changes is copied first, so each change can be undone.
    // A change is only undone while the file still holds what the launcher wrote.
    public static class BackupService
    {
        public static string Root => Path.Combine(GamePaths.AppData, "Backups");

        private static string IndexPath => Path.Combine(Root, "backups.json");

        public static event Action? Changed;

        private static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..4];

        public static List<BackupEntry> Load()
        {
            try
            {
                return File.Exists(IndexPath)
                    ? JsonSerializer.Deserialize<List<BackupEntry>>(File.ReadAllText(IndexPath)) ?? new()
                    : new();
            }
            catch
            {
                return new();
            }
        }

        private static void Save(List<BackupEntry> entries)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            Changed?.Invoke();
        }

        public const string OutsideMessage = "Command Center only changes files in the game folder and in the game's Documents folder.";

        // Command Center changes files only inside the Zero Hour folder and the game's Documents folder, judged by where the
        // path really leads. Anything else (Windows, Program Files, a folder mistaken for the game, a junction that leads
        // out) is refused here, whatever asked for the change.
        public static void EnsureAllowed(string path)
        {
            if (!PathGuard.IsAllowed(path))
                throw new InvalidOperationException(OutsideMessage);
        }

        // The only registry key Command Center changes: Windows' compatibility layers for programs (Health's fixes)
        public const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

        public const string RegistryMessage = "Command Center only changes Windows' compatibility settings for programs in the registry.";

        // Keys under HKEY_CURRENT_USER whose values may be set and restored; one that ends in a backslash allows the keys
        // under it
        internal static readonly List<string> AllowedRegistryKeys = [LayersKey];

        private static bool IsAllowedRegistryKey(string key) =>
            AllowedRegistryKeys.Any(allowed => allowed.EndsWith('\\')
                ? key.Length > allowed.Length && key.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
                : key.Equals(allowed, StringComparison.OrdinalIgnoreCase));

        // An entry Command Center could have written: its files in the allowed folders, its copy in the backup folder, a
        // registry entry on an allowed key. backups.json is a file on disk, so an entry that points anywhere else is
        // never undone.
        private static bool IsSafe(BackupEntry entry) => entry.Kind switch
        {
            "registry" => IsAllowedRegistryKey(entry.Target) && !string.IsNullOrEmpty(entry.BackupFile),
            "readonly" => (entry.Files ?? []).All(PathGuard.IsAllowed),
            "move" => PathGuard.IsAllowed(entry.Target) && entry.BackupFile != null && PathGuard.IsWithin(entry.BackupFile, Path.Combine(Root, "Quarantine")),
            _ => PathGuard.IsAllowed(entry.Target) && (entry.BackupFile == null || PathGuard.IsWithin(entry.BackupFile, Root)),
        };

        public const string GameRunningMessage = "Close the game first. Command Center does not change the game's files while it is running.";

        // The game keeps its folder open and reads Options.ini and settings.json when it starts (and writes them itself),
        // so a change made while it runs is either refused by Windows or lost. Refused here, whatever asked for it.
        public static void EnsureGameClosed(string path)
        {
            string full = Path.GetFullPath(path);
            bool inGame = GamePaths.ZeroHourFound && full.StartsWith(GamePaths.Game.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            bool readByGame = string.Equals(full, Path.GetFullPath(GamePaths.Options), StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, Path.GetFullPath(GamePaths.GoSettings), StringComparison.OrdinalIgnoreCase);
            if ((inGame || readByGame) && GameLauncher.IsGameRunning())
                throw new InvalidOperationException(GameRunningMessage);
        }

        public static BackupEntry WriteFile(string title, string target, byte[] content, string detail = "") =>
            OperationLog.Record("write", target, OperationLog.Describe(title, detail), () => Write(title, target, content, detail));

        private static BackupEntry Write(string title, string target, byte[] content, string detail)
        {
            EnsureAllowed(target);
            EnsureGameClosed(target);
            Directory.CreateDirectory(Root);
            var entry = new BackupEntry
            {
                Id = NewId(),
                Title = title,
                Detail = detail,
                Target = target,
                When = DateTime.Now,
                AfterHash = Hash(content),
            };

            if (File.Exists(target))
            {
                entry.BackupFile = Path.Combine(Root, $"{entry.Id}-{Path.GetFileName(target)}");
                File.Copy(target, entry.BackupFile, overwrite: true);
            }

            string folder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            // A new file under a name nobody can guess, so nothing placed beside the target beforehand can take the write
            string temp = Path.Combine(folder, $"{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    file.Write(content);
                File.Move(temp, target, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            var entries = Load();
            entries.Add(entry);
            Save(entries);
            return entry;
        }

        // A file where there was none, such as an installed map's: nothing to back up, so no Undo entry, but the place is
        // checked like every change. A file that is already there is refused; WriteFile backs it up first.
        public static void CreateFile(string target, byte[] content)
        {
            EnsureAllowed(target);
            EnsureGameClosed(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                file.Write(content);
            }
            catch
            {
                file.Dispose();
                TryDelete(target);
                throw;
            }
            file.Dispose();
        }

        // Moves a file into the launcher's quarantine folder; undo moves it back.
        public static BackupEntry MoveToQuarantine(string title, string path, string detail = "") =>
            OperationLog.Record("quarantine", path, OperationLog.Describe(title, detail), () => Quarantine(title, path, detail));

        private static BackupEntry Quarantine(string title, string path, string detail)
        {
            EnsureAllowed(path);
            EnsureGameClosed(path);
            string id = NewId();
            string folder = Path.Combine(Root, "Quarantine", id);
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, Path.GetFileName(path));
            try
            {
                File.Move(path, destination);
            }
            catch
            {
                try { Directory.Delete(folder); } catch { }
                throw;
            }

            var entry = new BackupEntry { Id = id, Title = title, Detail = detail, Kind = "move", Target = path, BackupFile = destination, When = DateTime.Now };
            var entries = Load();
            entries.Add(entry);
            Save(entries);
            return entry;
        }

        // Sets (or deletes, when newValue is null) a string value under HKEY_CURRENT_USER, keeping the old one.
        public static BackupEntry SetUserRegistryValue(string title, string key, string valueName, string? newValue, string detail = "") =>
            OperationLog.Record("registry", $@"HKCU\{key}\{valueName}", OperationLog.Describe(title, detail),
                () => SetRegistry(title, key, valueName, newValue, detail));

        private static BackupEntry SetRegistry(string title, string key, string valueName, string? newValue, string detail)
        {
            if (!IsAllowedRegistryKey(key))
                throw new InvalidOperationException(RegistryMessage);
            using var regKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
            string? old = regKey.GetValue(valueName) as string;
            if (newValue == null)
                regKey.DeleteValue(valueName, throwOnMissingValue: false);
            else
                regKey.SetValue(valueName, newValue);

            var entry = new BackupEntry
            {
                Id = NewId(),
                Title = title,
                Detail = detail,
                Kind = "registry",
                Target = key,
                BackupFile = valueName,
                OldValue = old,
                NewValue = newValue,
                When = DateTime.Now,
            };
            var entries = Load();
            entries.Add(entry);
            Save(entries);
            return entry;
        }

        // Clears the read-only flag on files; undo sets it again on the same files.
        public static BackupEntry ClearReadOnly(string title, string folder, IEnumerable<string> paths, string detail = "") =>
            OperationLog.Record("clear read-only", folder, OperationLog.Describe(title, detail), () => ClearReadOnlyFlags(title, folder, paths, detail));

        private static BackupEntry ClearReadOnlyFlags(string title, string folder, IEnumerable<string> paths, string detail)
        {
            var cleared = new List<string>();
            try
            {
                foreach (string path in paths)
                {
                    EnsureAllowed(path);
                    EnsureGameClosed(path);
                    var attributes = File.GetAttributes(path);
                    if (!attributes.HasFlag(FileAttributes.ReadOnly))
                        continue;
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    cleared.Add(path);
                }
            }
            catch
            {
                // Leave nothing half done
                SetReadOnly(cleared);
                throw;
            }

            var entry = new BackupEntry { Id = NewId(), Title = title, Detail = detail, Kind = "readonly", Target = folder, Files = cleared, When = DateTime.Now };
            var entries = Load();
            entries.Add(entry);
            Save(entries);
            return entry;
        }

        private static void SetReadOnly(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                }
                catch { }
            }
        }

        public static BackupEntry? Latest(string title) =>
            Load().Where(e => e.Title == title).OrderByDescending(e => e.When).FirstOrDefault();

        public static bool CanUndo(BackupEntry entry)
        {
            try
            {
                if (!IsSafe(entry))
                    return false;
                switch (entry.Kind)
                {
                    case "move":
                        return entry.BackupFile != null && File.Exists(entry.BackupFile) && !File.Exists(entry.Target);
                    case "registry":
                        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(entry.Target))
                            return (key?.GetValue(entry.BackupFile!) as string) == entry.NewValue;
                    case "readonly":
                        var present = (entry.Files ?? new()).Where(File.Exists).ToList();
                        return present.Count > 0 && present.All(f => !File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly));
                    default:
                        return File.Exists(entry.Target) && Hash(File.ReadAllBytes(entry.Target)) == entry.AfterHash;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Undo(BackupEntry entry)
        {
            if (!CanUndo(entry))
                return false;

            OperationLog.Record("undo", entry.Target, OperationLog.Describe(entry.Title, entry.Detail), () =>
            {
                Restore(entry);
                var entries = Load();
                entries.RemoveAll(e => e.Id == entry.Id);
                Save(entries);
            });
            return true;
        }

        // Puts the file back the way it was before the launcher first changed it.
        public static bool RestoreOriginal(string target)
        {
            EnsureAllowed(target);
            var entries = Load();
            var forTarget = entries.Where(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.When)
                .ToList();
            if (forTarget.Count == 0)
                return false;

            OperationLog.Record("restore original", target, forTarget[0].Title, () =>
            {
                Restore(forTarget[0]);
                foreach (var entry in forTarget.Skip(1).Where(e => e.BackupFile != null && e.Kind == "file" && PathGuard.IsWithin(e.BackupFile, Root)))
                    TryDelete(entry.BackupFile!);
                entries.RemoveAll(e => forTarget.Contains(e));
                Save(entries);
            });
            return true;
        }

        public static bool HasChanges(string target) =>
            Load().Any(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase));

        private static void Restore(BackupEntry entry)
        {
            if (!IsSafe(entry))
                throw new InvalidOperationException(entry.Kind == "registry" ? RegistryMessage : OutsideMessage);
            if (entry.Kind != "registry")
                EnsureGameClosed(entry.Kind == "readonly" ? Path.Combine(entry.Target, "_") : entry.Target);

            if (entry.Kind == "move")
            {
                if (entry.BackupFile != null && File.Exists(entry.BackupFile))
                    File.Move(entry.BackupFile, entry.Target);
                return;
            }
            if (entry.Kind == "registry")
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(entry.Target);
                if (entry.OldValue == null)
                    key.DeleteValue(entry.BackupFile!, throwOnMissingValue: false);
                else
                    key.SetValue(entry.BackupFile!, entry.OldValue);
                return;
            }
            if (entry.Kind == "readonly")
            {
                SetReadOnly(entry.Files ?? new());
                return;
            }

            if (entry.BackupFile != null && File.Exists(entry.BackupFile))
            {
                File.Copy(entry.BackupFile, entry.Target, overwrite: true);
                TryDelete(entry.BackupFile);
            }
            else if (entry.BackupFile == null)
            {
                // The launcher created this file; undo sends it to the Recycle Bin
                RecycleBin.SendFile(entry.Target);
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    }

    public static class RecycleBin
    {
        public static void SendFile(string path)
        {
            if (File.Exists(path))
                OperationLog.Record("recycle", path, "", () =>
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
        }

        public static void SendFolder(string path)
        {
            if (Directory.Exists(path))
                OperationLog.Record("recycle", path, "", () =>
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
        }
    }
}
