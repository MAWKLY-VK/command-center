using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CommandCenter.Services
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

        public static BackupEntry WriteFile(string title, string target, byte[] content, string detail = "")
        {
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

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".tmp";
            File.WriteAllBytes(temp, content);
            File.Move(temp, target, overwrite: true);

            var entries = Load();
            entries.Add(entry);
            Save(entries);
            return entry;
        }

        // Moves a file into the launcher's quarantine folder; undo moves it back.
        public static BackupEntry MoveToQuarantine(string title, string path, string detail = "")
        {
            string id = NewId();
            string folder = Path.Combine(Root, "Quarantine", id);
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, Path.GetFileName(path));
            File.Move(path, destination);

            var entry = new BackupEntry { Id = id, Title = title, Detail = detail, Kind = "move", Target = path, BackupFile = destination, When = DateTime.Now };
            var entries = Load();
            entries.Add(entry);
            Save(entries);
            return entry;
        }

        // Sets (or deletes, when newValue is null) a string value under HKEY_CURRENT_USER, keeping the old one.
        public static BackupEntry SetUserRegistryValue(string title, string key, string valueName, string? newValue, string detail = "")
        {
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
        public static BackupEntry ClearReadOnly(string title, string folder, IEnumerable<string> paths, string detail = "")
        {
            var cleared = new List<string>();
            try
            {
                foreach (string path in paths)
                {
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

            Restore(entry);
            var entries = Load();
            entries.RemoveAll(e => e.Id == entry.Id);
            Save(entries);
            return true;
        }

        // Puts the file back the way it was before the launcher first changed it.
        public static bool RestoreOriginal(string target)
        {
            var entries = Load();
            var forTarget = entries.Where(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.When)
                .ToList();
            if (forTarget.Count == 0)
                return false;

            Restore(forTarget[0]);
            foreach (var entry in forTarget.Skip(1).Where(e => e.BackupFile != null && e.Kind == "file"))
                TryDelete(entry.BackupFile!);
            entries.RemoveAll(e => forTarget.Contains(e));
            Save(entries);
            return true;
        }

        public static bool HasChanges(string target) =>
            Load().Any(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase));

        private static void Restore(BackupEntry entry)
        {
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
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        public static void SendFolder(string path)
        {
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }
}
