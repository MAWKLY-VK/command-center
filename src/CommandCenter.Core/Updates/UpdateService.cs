using System.ComponentModel;
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

    // What one update check found: an update, or why the check could not run, or neither (this is the newest version)
    public sealed record UpdateCheck(UpdateInfo? Update, string? Failure);

    // The new version could not start, so the previous one was put back and this copy keeps running (P3-D1)
    public sealed class PreviousVersionRestoredException(string message, Exception inner) : Exception(message, inner);

    // Mandatory updates. Each release carries an update.json next to the program. Its old fields (version, url, sha256,
    // notes) are for 0.4.0; this version reads only the signed payload (ADR 0007, Format). When that names a newer
    // version, this copy stops working until the new program is installed. The previous program stays beside the new
    // one as <name>.old.exe until the new one has started successfully (P3-D1, ADR 0009).
    public static class UpdateService
    {
        public const string ManifestUrl = "https://github.com/MAWKLY-VK/command-center/releases/latest/download/update.json";
        public const string ReleasePage = "https://github.com/MAWKLY-VK/command-center/releases/latest";

        // A signed update may only download from this repository's releases
        public const string ReleaseDownloads = "https://github.com/MAWKLY-VK/command-center/releases/download/";

        // The two steps a copy of Command Center started through Windows' administrator prompt may do, the only elevation
        // in Command Center: this program's file replaces itself with a checked download, and the previous version kept
        // beside it puts itself back
        public const string ReplaceArgument = "--replace-program";
        public const string PutBackArgument = "--put-back-program";

        // How such a copy ends
        private const int StepDone = 0;
        private const int StepWrongArguments = 2;
        private const int StepNotTheDownload = 3;
        private const int StepCannotChange = 4;

        // Windows' code when the player refuses the administrator prompt
        private const int ErrorCancelled = 1223;
        // Far longer than the step takes (it reads and writes one file), so only a copy that hangs reaches it
        private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(10);

        // Test builds (and the tests) also accept a download from a local file, to try the update path; release builds
        // never set it
        internal static bool AllowLocalDownloads { get; set; }

        // A version that could not start in this run; it is not offered again until Command Center starts again
        private static Version? _failedVersion;

        // Set once this copy's own file became the previous version beside a new one; it is not removed then
        private static bool _replacedThisRun;

        // The program's own version: CommandCenter.exe, whose <Version> tools/publish.ps1 also reads. Not this
        // assembly's: the services live in CommandCenter.Core.dll, which has no version of its own.
        public static Version Current => Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));

        // Reads the manifest from a web address or a local file. Offline, missing or broken manifests mean "no update",
        // so the program keeps working. A manifest that breaks a rule is refused, and the operation log says why.
        public static async Task<UpdateInfo?> CheckAsync(string source) => (await CheckNowAsync(source)).Update;

        // The check with its outcome: the update offered, or why the check could not run (Failure, in plain words), or
        // neither when this is the newest version. Settings shows the failure instead of "latest version" (ticket 08).
        public static async Task<UpdateCheck> CheckNowAsync(string source)
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
                return new UpdateCheck(null, "The update information could not be read. Check the internet connection.");
            }
            if (Read(manifest, out var update) is { } refusal)
            {
                OperationLog.Failed("check update", source, refusal);
                return new UpdateCheck(null, "The update information was refused: " + refusal);
            }
            return new UpdateCheck(update != null && update.Version == _failedVersion ? null : update, null);
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

        // Before anything is downloaded: whether changing the program's folder needs administrator rights. Fails when
        // Command Center was not started from its own program file, or when its folder cannot be written at all.
        public static bool FolderNeedsAdministrator()
        {
            var (staged, _, _) = Siblings(ProgramFile());
            try
            {
                using (File.Create(staged)) { }
                File.Delete(staged);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
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

        // Puts the downloaded program in place of this one, through Windows' administrator prompt when the folder needs
        // it. This copy's file stays beside the new one as the previous version. The download is removed either way.
        public static Task InstallAsync(string downloaded, UpdateInfo update, bool asAdministrator) =>
            OperationLog.RecordAsync("install update", Environment.ProcessPath ?? "",
                "From " + downloaded + (asAdministrator ? ", as administrator" : ""), async () =>
                {
                    string exe = ProgramFile();
                    if (asAdministrator)
                    {
                        try
                        {
                            int code;
                            try
                            {
                                code = await Task.Run(() => RunAsAdministrator(exe, [ReplaceArgument, downloaded, update.Sha256]));
                            }
                            catch (TimeoutException) when (HoldsUpdate(exe, update.Sha256))
                            {
                                // The step finished its swap after the wait gave up: the folder says what happened
                                code = StepDone;
                            }
                            if (code != StepDone)
                                throw new IOException(StepFailure(code));
                        }
                        finally
                        {
                            TryDelete(downloaded);
                        }
                    }
                    else
                    {
                        ReplaceProgram(exe, downloaded);
                    }
                    _replacedThisRun = true;
                    return true;
                });

        // In a folder this copy can write: the download is copied in as <name>.new.exe (a copy takes the folder's
        // permissions, where a move would keep those of the private temporary folder), then the files are swapped
        internal static void ReplaceProgram(string exe, string downloaded)
        {
            var (staged, old, _) = Siblings(exe);
            try
            {
                File.Copy(downloaded, staged, overwrite: true);
                Swap(exe, staged, old);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Only while the program is in place: if putting it back failed too, the new one is all that is left
                if (File.Exists(exe))
                    TryDelete(staged);
                throw new IOException("Could not replace the program file: " + ex.Message, ex);
            }
            finally
            {
                TryDelete(downloaded);
            }
        }

        // The administrator copy's replace. It reads the download once and checks it against the SHA-256 the update
        // screen gave before anything is written, so it can place only that program in the folder, and nothing it read
        // is left there when the check fails.
        internal static int ReplaceAsAdministrator(string exe, string downloaded, string sha256)
        {
            if (!IsLocalPath(downloaded) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                return StepWrongArguments;
            byte[] program;
            try
            {
                program = BoundedFile.Read(downloaded, DownloadService.UpdateProgramLimit);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return StepNotTheDownload;
            }
            if (!Convert.ToHexString(SHA256.HashData(program)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                return StepNotTheDownload;
            var (staged, old, _) = Siblings(exe);
            try
            {
                File.WriteAllBytes(staged, program);
                Swap(exe, staged, old);
                return StepDone;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(exe))
                    TryDelete(staged);
                return StepCannotChange;
            }
        }

        // The running program can be renamed but not deleted, so it moves aside as the previous version, where it stays
        // until the new one has started. A failed step leaves the program as it was.
        private static void Swap(string exe, string staged, string old)
        {
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

        // Starts the new program with the same arguments; the caller then closes this one. When Windows cannot start it
        // at all, the previous version is put back and this copy keeps running.
        public static Task RestartAsync(Version version) =>
            OperationLog.RecordAsync("restart", Environment.ProcessPath ?? "", "Version " + version.ToString(3), () => Task.Run(() =>
            {
                RestartOrPutBack(ProgramFile(), version, () =>
                {
                    // The test switch is not passed on, so a wrong manifest cannot cause an update loop. The new copy waits
                    // for this one to close before it takes the single-copy lock.
                    var args = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.Equals("--update-auto", StringComparison.OrdinalIgnoreCase));
                    ProcessRunner.Start(new StartRequest(StartKind.Restart, "", SingleInstance.ArgumentsForRestart(args)))?.Dispose();
                });
                return true;
            }));

        // When Windows cannot start the new program at all (the file is missing, an antivirus blocked it, it is not a
        // valid program), the previous version is put back and that version is not offered again in this run. A new
        // version that starts and then fails is put back by hand, as README explains.
        internal static void RestartOrPutBack(string exe, Version version, Action start)
        {
            try
            {
                start();
            }
            catch (Exception error)
            {
                _failedVersion = Normalize(version);
                string failed = $"Version {version.ToString(3)} could not start ({WhyNotStarted(error)})";
                try
                {
                    PutBackOrAsk(exe);
                }
                catch (Exception putBack)
                {
                    throw new IOException($"{failed}, and the previous version could not be put back ({putBack.Message.TrimEnd('.')}). "
                        + "The download page explains how to put it back by hand.", error);
                }
                throw new PreviousVersionRestoredException($"{failed}, so the previous version was put back.", error);
            }
        }

        // Whether the program file is now the update with this SHA-256
        private static bool HoldsUpdate(string exe, string sha256)
        {
            try
            {
                using var file = File.OpenRead(exe);
                return Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Why Windows did not start a program, in plain words and without the paths .NET puts around its reason: the cases
        // the spec names first, then Windows' own text
        private static string WhyNotStarted(Exception error) => error is not Win32Exception windows
            ? error.Message.TrimEnd('.')
            : windows.NativeErrorCode switch
            {
                2 or 3 => "the file is missing",
                5 => "Windows or an antivirus blocked it",
                193 or 216 => "it is not a valid program for this PC",
                225 or 226 => "an antivirus blocked it",
                1260 => "a Windows policy blocked it",
                int code => new Win32Exception(code).Message.Replace("%1", "the program").TrimEnd('.'),
            };

        private static void PutBackOrAsk(string exe)
        {
            try
            {
                PutBack(exe);
            }
            catch (UnauthorizedAccessException)
            {
                int code = RunAsAdministrator(PreviousVersionOf(exe), [PutBackArgument]);
                if (code != StepDone)
                    throw new IOException(StepFailure(code));
            }
        }

        // The new program moves aside as <name>.failed.exe (renaming works where deleting may not), the previous version
        // takes its name, and the failed file is removed now or at the next start
        private static void PutBack(string exe)
        {
            var (_, old, failed) = Siblings(exe);
            if (!File.Exists(old))
                throw new FileNotFoundException("The previous version is not beside the program.", old);
            bool present = File.Exists(exe);
            if (present)
                File.Move(exe, failed, overwrite: true);
            try
            {
                File.Move(old, exe);
            }
            catch
            {
                if (present)
                    File.Move(failed, exe);
                throw;
            }
            TryDelete(failed);
        }

        // Starts a copy of Command Center as administrator for one step and waits for it to end. Windows asks the player
        // first.
        private static int RunAsAdministrator(string program, string[] arguments)
        {
            Process? process;
            try
            {
                process = ProcessRunner.Start(new StartRequest(StartKind.AsAdministrator, program, arguments));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                throw new IOException("Windows asked for permission, and it was not given.", ex);
            }
            using (process)
            {
                if (process == null)
                    throw new IOException("The administrator step did not start.");
                if (!process.WaitForExit(StepTimeout))
                    throw new TimeoutException("The administrator step did not finish.");
                return process.ExitCode;
            }
        }

        private static string StepFailure(int code) => code switch
        {
            StepNotTheDownload => "The download could not be read or failed its SHA-256 check, so it was not installed.",
            StepCannotChange => "Could not change the program file in its folder.",
            _ => $"Could not change the program file: the administrator step ended with code {code}.",
        };

        // The copy Windows starts as administrator for one step of an update. App runs this before anything else, so
        // that copy shows no window, takes no lock and reads no settings. Returns its exit code, or null for a normal
        // start.
        public static int? AdministratorStep(string[] commandLine)
        {
            if (commandLine.Length < 2)
                return null;
            bool replace = commandLine[1].Equals(ReplaceArgument, StringComparison.OrdinalIgnoreCase);
            if (!replace && !commandLine[1].Equals(PutBackArgument, StringComparison.OrdinalIgnoreCase))
                return null;
            try
            {
                if (replace)
                    return commandLine.Length == 4 ? ReplaceAsAdministrator(ProgramFile(), commandLine[2], commandLine[3]) : StepWrongArguments;
                if (commandLine.Length != 2 || ProgramOf(Environment.ProcessPath ?? "") is not { } exe)
                    return StepWrongArguments;
                PutBack(exe);
                return StepDone;
            }
            catch
            {
                return StepCannotChange;
            }
        }

        // Once this version has stayed open for 30 seconds or closed normally, the previous one beside it goes. In a folder
        // that needs administrator rights it stays until the next update replaces it: Command Center does not ask for
        // rights for this.
        public static void RemoveOldProgram()
        {
            if (_replacedThisRun)
                return;
            try
            {
                RemoveOldProgram(ProgramFile());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next start
            }
        }

        internal static void RemoveOldProgram(string exe)
        {
            string old = PreviousVersionOf(exe);
            if (!File.Exists(old))
                return;
            File.Delete(old);
            OperationLog.Done("remove previous version", old);
        }

        // At start: removes what an interrupted update left beside the program. The previous version stays until this
        // one has started successfully.
        public static void CleanUp()
        {
            string exe;
            try
            {
                exe = ProgramFile();
            }
            catch (IOException)
            {
                return;
            }
            _ = Task.Run(() => CleanUpLeftovers(exe));
        }

        internal static void CleanUpLeftovers(string exe)
        {
            var (staged, _, failed) = Siblings(exe);
            TryDelete(staged);
            TryDelete(failed);
        }

        // For the tests: a new run
        internal static void ForgetFailedVersion() => _failedVersion = null;

        internal static string PreviousVersionOf(string exe) => Siblings(exe).Old;

        private static string ProgramFile()
        {
            string? exe = Environment.ProcessPath;
            if (exe == null || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Command Center was not started from its own program file, so it cannot replace itself.");
            return exe;
        }

        // Beside the program: the download being swapped in, the previous version, and a new version that could not start
        private static (string Staged, string Old, string Failed) Siblings(string exe)
        {
            string folder = Path.GetDirectoryName(exe)!;
            string name = Path.GetFileNameWithoutExtension(exe);
            return (Path.Combine(folder, name + ".new.exe"), Path.Combine(folder, name + ".old.exe"), Path.Combine(folder, name + ".failed.exe"));
        }

        // The program a previous version belongs to: <name>.old.exe beside <name>.exe
        private static string? ProgramOf(string previous)
        {
            const string Suffix = ".old.exe";
            string name = Path.GetFileName(previous);
            return name.Length > Suffix.Length && name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Path.GetDirectoryName(previous)!, name[..^Suffix.Length] + ".exe")
                : null;
        }

        // A plain path on a local drive, not a network or device path
        private static bool IsLocalPath(string path) =>
            Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal);

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
