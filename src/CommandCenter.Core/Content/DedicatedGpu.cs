using System.IO;
using System.Security.Cryptography;

namespace CommandCenter.Core.Content
{
    public enum DedicatedGpuState { Off, On, Changed }

    // dxwrapper_go's download: where it is, its SHA-256 and size, and the SHA-256 of each file it must hold, by name
    public sealed record WrapperPackage(Uri Url, string Sha256, long Size, IReadOnlyDictionary<string, string> Files);

    // The dedicated graphics card (ADR 0010): Generals Online's client loads dxwrapper_go.dll from its own folder when it is
    // there, and then runs on the faster of two graphics cards. Turning it on downloads the package, checks it and each of
    // its two files against their pinned SHA-256, and writes them into the game folder through BackupService (each with
    // Undo, an existing file backed up first), never while the game runs. Turning it off moves them to quarantine.
    public sealed class DedicatedGpu(WrapperPackage package, Func<Uri, long, Task<byte[]>> download)
    {
        public const string DllName = "dxwrapper_go.dll";
        public const string IniName = "dxwrapper_go.ini";

        // The build of elishacloud/dxwrapper that the Generals Online team provides, attached to Command Center's 0.5.0
        // release under the asset name ADR 0009 keeps
        public static readonly WrapperPackage Release = new(
            new Uri(UpdateService.ReleaseDownloads + "v0.5.0/dxwrapper_go.zip"),
            "1F71ABAF1D12BD9D74D4B4A076B5D900AD0DD0D086EB3E69C21F890F8223557A",
            3_452_656,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [DllName] = "0880EA4293EE58173AE13FB46D059661AB8F0C754ABC65C159C74E22412F01EE",
                [IniName] = "F92842100A736BA5638C061D38692968A374CCB2929C336CB9F5AEEAAF97E902",
            });

        public static DedicatedGpu Default { get; } = new(Release, (url, limit) => DownloadService.GetBytesAsync(url, limit));

        // A file of the package is a few megabytes; anything far larger is not one
        private const long FileLimit = 32L * 1024 * 1024;

        private static readonly ExtractRules Rules = new(
            name => name.Equals(DllName, StringComparison.OrdinalIgnoreCase) || name.Equals(IniName, StringComparison.OrdinalIgnoreCase), FileLimit);

        // On: both files are the pinned ones. Changed: one or both are there but differ (edited by hand, or another build).
        public DedicatedGpuState State(string gameFolder)
        {
            var present = package.Files.Where(file => File.Exists(Path.Combine(gameFolder, file.Key))).ToList();
            if (present.Count == 0)
                return DedicatedGpuState.Off;
            bool ours = present.Count == package.Files.Count && present.All(file => Matches(Path.Combine(gameFolder, file.Key), file.Value));
            return ours ? DedicatedGpuState.On : DedicatedGpuState.Changed;
        }

        // The changes it made, newest last; a file that is already the pinned one is left alone
        public async Task<IReadOnlyList<BackupEntry>> EnableAsync(string gameFolder, IProgress<string>? progress = null)
        {
            // Refused before anything is downloaded while the game runs
            BackupService.EnsureGameClosed(Path.Combine(gameFolder, DllName));

            progress?.Report("Downloading dxwrapper_go");
            byte[] zip = await download(package.Url, package.Size);
            if (!Hash(zip).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download does not match dxwrapper_go's pinned SHA-256, so nothing was changed.");

            string staging = ArchiveExtractor.Extract(new MemoryStream(zip), Rules);
            try
            {
                var files = new List<(string Name, byte[] Content)>();
                foreach (var (name, sha) in package.Files)
                {
                    string path = Path.Combine(staging, name);
                    if (!File.Exists(path))
                        throw new InvalidDataException($"The download has no {name}, so nothing was changed.");
                    byte[] content = BoundedFile.Read(path, FileLimit);
                    if (!Hash(content).Equals(sha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{name} does not match its pinned SHA-256, so nothing was changed.");
                    files.Add((name, content));
                }

                progress?.Report("Copying dxwrapper_go into the game folder");
                var made = new List<BackupEntry>();
                try
                {
                    foreach (var (name, content) in files)
                    {
                        string target = Path.Combine(gameFolder, name);
                        if (!Matches(target, package.Files[name]))
                            made.Add(BackupService.WriteFile("Dedicated graphics card on", target, content, name));
                    }
                }
                catch
                {
                    // Half a package is worse than none: what was written goes back
                    foreach (var entry in Enumerable.Reverse(made))
                        BackupService.Undo(entry);
                    throw;
                }
                return made;
            }
            finally
            {
                ArchiveExtractor.Discard(staging);
            }
        }

        // Both files go to quarantine, each with Undo
        public IReadOnlyList<BackupEntry> Disable(string gameFolder)
        {
            var moved = new List<BackupEntry>();
            foreach (string name in new[] { IniName, DllName })
            {
                string path = Path.Combine(gameFolder, name);
                if (File.Exists(path))
                    moved.Add(BackupService.MoveToQuarantine("Dedicated graphics card off", path, name));
            }
            return moved;
        }

        private static bool Matches(string path, string sha)
        {
            try
            {
                return File.Exists(path) && Hash(BoundedFile.Read(path, FileLimit)).Equals(sha, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return false;
            }
        }

        private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    }
}
