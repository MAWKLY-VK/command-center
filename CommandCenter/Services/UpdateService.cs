using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CommandCenter.Services
{
    public sealed record UpdateInfo(Version Version, string VersionText, string Url, string Sha256, string Notes);

    // Mandatory updates. Each release carries an update.json next to the program:
    // { "version": "0.2.0", "url": "https://.../CommandCenter.exe", "sha256": "<hex>", "notes": "..." }
    // When it names a newer version, this copy stops working until the new program is installed.
    public static class UpdateService
    {
        public const string ManifestUrl = "https://github.com/MAWKLY-VK/command-center/releases/latest/download/update.json";
        public const string ReleasePage = "https://github.com/MAWKLY-VK/command-center/releases/latest";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // No overall timeout: the download can take minutes, so each step has its own limit instead
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CommandCenter/" + Current.ToString(3));
            return client;
        }

        public static Version Current => Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0));

        // Reads the manifest from a web address or a local file. Offline, missing or broken manifests
        // mean "no update" so the program keeps working.
        public static async Task<UpdateInfo?> CheckAsync(string source)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                bool web = IsWeb(source);
                string json = web
                    ? await Http.GetStringAsync(source, timeout.Token)
                    : await File.ReadAllTextAsync(LocalPath(source), timeout.Token);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string versionText = Text(root, "version");
                string url = Text(root, "url");
                string sha = Text(root, "sha256").ToLowerInvariant();
                if (ParseVersion(versionText) is not { } version || version <= Current)
                    return null;
                if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                    return null;
                // A manifest from the web may only point at a web download; local files are for testing
                if (url.Length == 0 || (web && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    return null;
                return new UpdateInfo(version, versionText.Trim(), url, sha, Text(root, "notes").Trim());
            }
            catch
            {
                return null;
            }
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

        // Downloads the new program to a temporary file and checks its SHA-256. Returns the file's path.
        public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<(long Done, long Total)> progress)
        {
            string temp = Path.Combine(Path.GetTempPath(), $"CommandCenter-{update.Version.ToString(3)}.exe.download");
            using var stall = new CancellationTokenSource();
            HttpResponseMessage? response = null;
            Stream? source = null;
            try
            {
                long total;
                stall.CancelAfter(TimeSpan.FromSeconds(30));
                if (IsWeb(update.Url))
                {
                    response = await Http.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                    response.EnsureSuccessStatusCode();
                    total = response.Content.Headers.ContentLength ?? -1;
                    source = await response.Content.ReadAsStreamAsync(stall.Token);
                }
                else
                {
                    source = File.OpenRead(LocalPath(update.Url));
                    total = source.Length;
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var target = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int shown = -1;
                    while (true)
                    {
                        // Gives up when no data arrives for 30 seconds
                        stall.CancelAfter(TimeSpan.FromSeconds(30));
                        int read = await source.ReadAsync(buffer, stall.Token);
                        if (read == 0)
                            break;
                        await target.WriteAsync(buffer.AsMemory(0, read));
                        hash.AppendData(buffer, 0, read);
                        done += read;
                        int percent = total > 0 ? (int)(done * 100 / total) : (int)(done >> 20);
                        if (percent != shown)
                        {
                            shown = percent;
                            progress.Report((done, total));
                        }
                    }
                    progress.Report((done, total));
                }

                string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actual != update.Sha256)
                    throw new InvalidDataException("The downloaded file failed its SHA-256 check, so it was not installed.");
                return temp;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                throw ex switch
                {
                    InvalidDataException => ex,
                    OperationCanceledException => new IOException("The download stopped responding."),
                    _ => new IOException("Could not download the update: " + ex.Message, ex),
                };
            }
            finally
            {
                source?.Dispose();
                response?.Dispose();
            }
        }

        // Puts the downloaded program in place of this one. Windows lets a running program be renamed but not
        // deleted, so this copy is moved aside as <name>.old.exe and removed by the next start.
        public static void Install(string downloaded)
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
        public static void Restart()
        {
            var (exe, _, _) = Files();
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
            {
                // The test switch is not passed on, so a wrong manifest cannot cause an update loop
                if (!arg.Equals("--update-auto", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(arg);
            }
            Process.Start(start)?.Dispose();
        }

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
