using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CommandCenter.Core.Updates
{
    public sealed record UpdateInfo(Version Version, string VersionText, string Url, string Sha256, string Notes);

    // Mandatory updates. Each release carries an update.json next to the program. Its old fields (version, url, sha256,
    // notes) are for 0.4.0; this version reads only the signed payload (ADR 0007, Format). When that names a newer
    // version, this copy stops working until the new program is installed.
    public static class UpdateService
    {
        public const string ManifestUrl = "https://github.com/MAWKLY-VK/command-center/releases/latest/download/update.json";
        public const string ReleasePage = "https://github.com/MAWKLY-VK/command-center/releases/latest";

        // A signed update may only download from this repository's releases
        public const string ReleaseDownloads = "https://github.com/MAWKLY-VK/command-center/releases/download/";

        // Test builds (and the tests) also accept a download from a local file, to try the update path; release builds
        // never set it
        internal static bool AllowLocalDownloads { get; set; }

        // The program's own version: CommandCenter.exe, whose <Version> tools/publish.ps1 also reads. Not this
        // assembly's: the services live in CommandCenter.Core.dll, which has no version of its own.
        public static Version Current => Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));

        // Reads the manifest from a web address or a local file. Offline, missing or broken manifests mean "no update",
        // so the program keeps working. A manifest that breaks a rule is refused, and the operation log says why.
        public static async Task<UpdateInfo?> CheckAsync(string source)
        {
            byte[] manifest;
            try
            {
                manifest = IsWeb(source)
                    ? await DownloadService.GetBytesAsync(new Uri(source), DownloadService.SmallFileLimit, TimeSpan.FromSeconds(8))
                    : await ReadLocalAsync(LocalPath(source));
            }
            catch
            {
                return null;
            }
            if (Read(manifest, out var update) is { } refusal)
                OperationLog.Failed("check update", source, refusal);
            return update;
        }

        // The update a manifest offers. Returns the reason when it breaks a rule; update is null then, and also when it
        // names no newer version.
        private static string? Read(byte[] manifest, out UpdateInfo? update)
        {
            update = null;
            if (Json(manifest) is not { } root)
                return "It is not a JSON object.";
            byte[] payload;
            try { payload = Convert.FromBase64String(Text(root, "signed")); }
            catch (FormatException) { return "Its signed part is damaged."; }
            if (!ManifestVerifier.Verify(SignedKind.Update, payload, Text(root, "signature"), out string reason))
                return reason;
            if (Json(payload) is not { } signed)
                return "Its signed part is not a JSON object.";
            foreach (string field in new[] { "version", "url", "sha256", "expires" })
            {
                if (Text(signed, field).Length == 0)
                    return $"It is missing {field}.";
            }
            string versionText = Text(signed, "version").Trim();
            if (ParseVersion(versionText) is not { } version)
                return "Its version cannot be read.";
            if (version <= Current)
                return null;
            if (!DateTime.TryParse(Text(signed, "expires"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires))
                return "Its expiry date cannot be read.";
            if (expires <= DateTime.UtcNow)
                return $"It expired on {expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
            string url = Text(signed, "url").Trim();
            if (!IsAllowedDownload(url))
                return "Its download is not one of Command Center's GitHub releases.";
            string sha = Text(signed, "sha256").Trim().ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                return "Its SHA-256 is not 64 hexadecimal characters.";
            update = new UpdateInfo(version, versionText, url, sha, Text(signed, "notes").Trim());
            return null;
        }

        // A release download of this repository (checked after the address is normalised, so dot segments cannot leave
        // it), or a local file in a test build
        private static bool IsAllowedDownload(string url)
        {
            if (!IsWeb(url))
                return AllowLocalDownloads;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && uri.Scheme == Uri.UriSchemeHttps
                   && uri.AbsoluteUri.StartsWith(ReleaseDownloads, StringComparison.Ordinal);
        }

        private static JsonElement? Json(byte[] data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static async Task<byte[]> ReadLocalAsync(string path)
        {
            if (new FileInfo(path).Length > DownloadService.SmallFileLimit)
                throw new InvalidDataException("The manifest is too large.");
            return await File.ReadAllBytesAsync(path);
        }

        // Fails early when the program's folder cannot be written, before anything is downloaded
        public static void CheckFolder()
        {
            var (_, staged, _) = Files();
            try
            {
                using (File.Create(staged)) { }
                File.Delete(staged);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new IOException("Command Center cannot write to the folder it runs from. Get the new version from the download page and replace the program by hand.");
            }
        }

        // Downloads the new program to a private temporary file, checks its SHA-256 and that it is the version the
        // manifest names. Returns the file's path.
        public static Task<string> DownloadAsync(UpdateInfo update, IProgress<(long Done, long Total)> progress) =>
            OperationLog.RecordAsync("download update", update.Url, "Version " + update.VersionText, () => DownloadFileAsync(update, progress));

        private static async Task<string> DownloadFileAsync(UpdateInfo update, IProgress<(long Done, long Total)> progress)
        {
            if (!IsAllowedDownload(update.Url))
                throw new InvalidDataException("The update's download is not one of Command Center's GitHub releases.");
            string file;
            try
            {
                file = IsWeb(update.Url)
                    ? await DownloadService.DownloadFileAsync(new Uri(update.Url), DownloadService.UpdateProgramLimit, update.Sha256,
                        (done, total) => progress.Report((done, total)), default, ".exe.download")
                    : await CopyLocalAsync(LocalPath(update.Url), update.Sha256, progress);
            }
            catch (ChecksumMismatchException)
            {
                throw new InvalidDataException("The downloaded file failed its SHA-256 check, so it was not installed.");
            }
            catch (TimeoutException)
            {
                throw new IOException("The download stopped responding.");
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new IOException("Could not download the update: " + ex.Message, ex);
            }

            // A wrong upload is caught here, before it replaces anything
            var info = FileVersionInfo.GetVersionInfo(file);
            string actual = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart).ToString(3);
            if (actual != update.Version.ToString(3))
            {
                TryDelete(file);
                throw new InvalidDataException($"The downloaded program is version {actual}, not {update.VersionText}, so it was not installed.");
            }
            return file;
        }

        // A test build's local download, with the same limit and checksum
        private static async Task<string> CopyLocalAsync(string source, string sha256, IProgress<(long Done, long Total)> progress)
        {
            string temp = DownloadService.NewTempPath(".exe.download");
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = new FileInfo(source).Length;
                await using (var input = File.OpenRead(source))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await DownloadService.CopyAsync(input, output, DownloadService.UpdateProgramLimit, hash, done => progress.Report((done, total)), default);
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ChecksumMismatchException("The download does not match its checksum.");
                return temp;
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        // Puts the downloaded program in place of this one. Windows lets a running program be renamed but not
        // deleted, so this copy is moved aside as <name>.old.exe and removed by the next start.
        public static void Install(string downloaded) =>
            OperationLog.Record("install update", Environment.ProcessPath ?? "", "From " + downloaded, () => ReplaceProgram(downloaded));

        private static void ReplaceProgram(string downloaded)
        {
            var (exe, staged, old) = Files();
            try
            {
                File.Move(downloaded, staged, overwrite: true);
                File.Delete(old);
                File.Move(exe, old);
                try
                {
                    File.Move(staged, exe);
                }
                catch
                {
                    File.Move(old, exe);
                    throw;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                TryDelete(downloaded);
                TryDelete(staged);
                throw new IOException("Could not replace the program file: " + ex.Message, ex);
            }
        }

        // Starts the new program with the same arguments; the caller then closes this one
        public static void Restart() =>
            OperationLog.Record("restart", Environment.ProcessPath ?? "", "After the update", () =>
            {
                // The test switch is not passed on, so a wrong manifest cannot cause an update loop. The new copy waits for
                // this one to close before it takes the single-copy lock.
                var args = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.Equals("--update-auto", StringComparison.OrdinalIgnoreCase));
                ProcessRunner.Start(new StartRequest(StartKind.Restart, "", SingleInstance.ArgumentsForRestart(args)))?.Dispose();
            });

        // Removes the copy an earlier update moved aside. That program may still be closing, so this keeps trying for a while.
        public static void CleanUp()
        {
            string old;
            try
            {
                old = Files().Old;
            }
            catch
            {
                return;
            }
            if (!File.Exists(old))
                return;
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 30 && File.Exists(old); i++)
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch
                    {
                        await Task.Delay(500);
                    }
                }
            });
        }

        private static (string Exe, string Staged, string Old) Files()
        {
            string? exe = Environment.ProcessPath;
            if (exe == null || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Command Center was not started from its own program file, so it cannot replace itself.");
            string folder = Path.GetDirectoryName(exe)!;
            string name = Path.GetFileNameWithoutExtension(exe);
            return (exe, Path.Combine(folder, name + ".new.exe"), Path.Combine(folder, name + ".old.exe"));
        }

        private static bool IsWeb(string source) =>
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        private static string LocalPath(string source) =>
            Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : Path.GetFullPath(source);

        private static string Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

        // Accepts "0.2.0", "v0.2.0" and "0.2.0-beta"; missing parts count as 0
        private static Version? ParseVersion(string text)
        {
            text = text.Trim().TrimStart('v', 'V');
            int cut = text.IndexOfAny(['-', '+']);
            if (cut >= 0)
                text = text[..cut];
            return Version.TryParse(text, out var version) ? Normalize(version) : null;
        }

        private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
