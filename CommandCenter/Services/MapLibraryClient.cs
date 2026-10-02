using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CommandCenter.Services
{
    // One map in a library's catalog.json
    public sealed class LibraryMap
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("players")] public int Players { get; set; }
        [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("file")] public string Zip { get; set; } = "";
        [JsonPropertyName("preview")] public string? Preview { get; set; }
        [JsonPropertyName("starts")] public List<double[]>? Starts { get; set; }
        [JsonPropertyName("customRules")] public bool CustomRules { get; set; }
        [JsonPropertyName("scripts")] public bool Scripts { get; set; }
    }

    public sealed class LibraryCatalog
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("generated")] public string? Generated { get; set; }
        [JsonPropertyName("maps")] public List<LibraryMap>? Maps { get; set; }

        public DateTime Published => DateTime.TryParse(Generated, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
    }

    // Catalog is null when nothing could be read, and Problem and Detail say why. A catalog that comes with a
    // Problem is the copy saved earlier, because the library could not be checked for new maps.
    public sealed record CatalogResult(LibraryCatalog? Catalog, string? Problem, string? Detail, DateTime? Checked);

    public sealed record InstallReport(List<string> Installed, int Skipped, List<string> Failed, bool Cancelled);

    // Where an install run is: map Number of Count and its name. Percent covers the whole run when there are several
    // maps, otherwise the one download. Copying is true once the download is checked and its files are being copied.
    public sealed record InstallProgress(int Number, int Count, string Name, int Percent, bool Copying);

    // The map library: catalog.json, maps/<id>.zip and previews/<id>.png, read from a web address or from a folder
    // that holds a copy. A web library's catalog and previews are cached in the launcher's own folder. Every zip is
    // checked against the catalog's SHA-256 before MapService installs it into the user's Maps folder.
    public sealed class MapLibraryClient
    {
        public const string OnlineLibrary = "https://raw.githubusercontent.com/MAWKLY-VK/zh-map-library/main/";

        // --map-library <address or folder>
        public static string? SourceOverride { get; set; }

        // Where the library is read from: --map-library, then --library-folder, then the saved choice (or the older
        // library folder setting), then the online library
        public static string CurrentSource =>
            Value(SourceOverride) ?? Value(AppState.LibraryFolder) ?? Value(AppSettings.Current.MapLibrary)
            ?? Value(AppSettings.Current.LibraryFolder) ?? OnlineLibrary;

        // A client for the current source, or null when it is a plain folder of maps (read with MapCatalog.Scan)
        public static MapLibraryClient? ForCurrentSource() => IsCatalogSource(CurrentSource) ? new MapLibraryClient(CurrentSource) : null;

        // Saves the user's choice: a web address or a folder, or null for the online library. It also replaces the
        // command-line choice for the rest of this run.
        public static void UseSource(string? source)
        {
            SourceOverride = null;
            AppState.LibraryFolder = null;
            AppSettings.Current.MapLibrary = Value(source);
            AppSettings.Current.LibraryFolder = null;
            AppSettings.Current.Save();
        }

        private static string? Value(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromDays(1);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
        private const long CatalogLimit = 32L * 1024 * 1024;
        private const long PreviewLimit = 4L * 1024 * 1024;
        private const long ZipLimit = 256L * 1024 * 1024;

        private static readonly Regex SafeId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");
        private static readonly Regex SafePath = new(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$");
        private static readonly Regex Hex64 = new(@"^[0-9A-Fa-f]{64}$");

        private readonly Uri? _web;
        private readonly string? _folder;
        private readonly ConcurrentDictionary<string, bool> _noPreview = new();
        private volatile bool _webDown;

        public MapLibraryClient(string source)
        {
            Source = source.Trim();
            if (IsWebAddress(Source))
                _web = new Uri(Source.EndsWith('/') ? Source : Source + "/");
            else
                _folder = Path.GetFullPath(Source);
        }

        public string Source { get; }
        public bool IsWeb => _web != null;
        public bool IsOnlineLibrary => _web != null && _web.AbsoluteUri.Equals(OnlineLibrary, StringComparison.OrdinalIgnoreCase);

        // "Online library", the web address or the folder
        public string Describe => IsOnlineLibrary ? "Online library" : _web != null ? _web.Host + _web.AbsolutePath.TrimEnd('/') : _folder!;

        public static bool IsWebAddress(string source) =>
            Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

        // A web address, or a folder that holds catalog.json; any other folder is a plain folder of maps
        public static bool IsCatalogSource(string source)
        {
            if (IsWebAddress(source))
                return true;
            try { return File.Exists(Path.Combine(source.Trim(), "catalog.json")); }
            catch { return false; }
        }

        public static bool IsInstalled(LibraryMap map) => Directory.Exists(Path.Combine(GamePaths.Maps, map.Name));

        // One folder per web library, so switching libraries never mixes their files
        private string CacheFolder
        {
            get
            {
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_web!.AbsoluteUri.ToLowerInvariant())))[..10].ToLowerInvariant();
                return Path.Combine(GamePaths.AppData, "cache", "map-library-" + key);
            }
        }

        // ── Catalog ──

        public async Task<CatalogResult> LoadCatalogAsync(bool refresh = false, CancellationToken ct = default)
        {
            if (_folder != null)
            {
                string path = Path.Combine(_folder, "catalog.json");
                try
                {
                    var catalog = Parse(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
                    return new CatalogResult(catalog, null, null, File.GetLastWriteTime(path));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    return new CatalogResult(null, "The map library in this folder can't be read", Reason(ex), null);
                }
            }

            string cached = Path.Combine(CacheFolder, "catalog.json");
            string etagFile = cached + ".etag";
            var saved = new FileInfo(cached);
            if (!refresh && saved.Exists && DateTime.UtcNow - saved.LastWriteTimeUtc < CatalogMaxAge)
            {
                try { return new CatalogResult(Parse(await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false)), null, null, saved.LastWriteTime); }
                catch (Exception) when (!ct.IsCancellationRequested) { }   // damaged copy: download it again
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_web!, "catalog.json"));
                if (saved.Exists && File.Exists(etagFile))
                    request.Headers.TryAddWithoutValidation("If-None-Match", File.ReadAllText(etagFile).Trim());
                using var response = await GoApi.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified && saved.Exists)
                {
                    var unchanged = Parse(await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false));
                    File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
                    return new CatalogResult(unchanged, null, null, DateTime.Now);
                }
                EnsureSuccess(response);

                using var buffer = new MemoryStream();
                await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await CopyAsync(stream, buffer, CatalogLimit, null, null, ct).ConfigureAwait(false);
                byte[] data = buffer.ToArray();
                var catalog = Parse(data);

                // Saved only once it reads correctly
                try
                {
                    Directory.CreateDirectory(CacheFolder);
                    string temp = cached + ".tmp";
                    await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
                    File.Move(temp, cached, overwrite: true);
                    if (response.Headers.ETag is { } etag)
                        File.WriteAllText(etagFile, etag.ToString());
                    else if (File.Exists(etagFile))
                        File.Delete(etagFile);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                return new CatalogResult(catalog, null, null, DateTime.Now);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                string reason = Reason(ex);
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
                    reason += " The library may be private or may have moved.";
                if (saved.Exists)
                {
                    try { return new CatalogResult(Parse(await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false)), "Couldn't check for new maps", reason, saved.LastWriteTime); }
                    catch (Exception) when (!ct.IsCancellationRequested) { }
                }
                string title = IsOnlineLibrary ? "The online map library can't be reached right now" : "This map library can't be reached right now";
                return new CatalogResult(null, title, reason, null);
            }
        }

        // Entries that could point outside the library or the Maps folder are left out
        private static LibraryCatalog Parse(byte[] data)
        {
            var json = data.AsSpan();
            if (json.StartsWith(Encoding.UTF8.Preamble))
                json = json[Encoding.UTF8.Preamble.Length..];
            var catalog = JsonSerializer.Deserialize<LibraryCatalog>(json) ?? throw new InvalidDataException("The catalog is empty.");
            if (catalog.SchemaVersion > 1)
                throw new InvalidDataException("This library was made for a newer Command Center. Update Command Center to read it.");
            if (catalog.SchemaVersion < 1 || catalog.Maps == null)
                throw new InvalidDataException("This is not a map library catalog.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            catalog.Maps = catalog.Maps.Where(m => m != null && IsValid(m) && names.Add(m.Name)).ToList();
            foreach (var map in catalog.Maps)
            {
                map.Starts = (map.Starts ?? new List<double[]>())
                    .Where(s => s != null && s.Length >= 2 && double.IsFinite(s[0]) && double.IsFinite(s[1]))
                    .Select(s => new[] { Math.Clamp(s[0], 0, 1), Math.Clamp(s[1], 0, 1) })
                    .Take(8)
                    .ToList();
            }
            return catalog;
        }

        private static bool IsValid(LibraryMap m) =>
            SafeId.IsMatch(m.Id ?? "") && !m.Id!.Contains("..")
            && IsSafeName(m.Name)
            && Hex64.IsMatch(m.Sha256 ?? "")
            && IsSafePath(m.Zip)
            && (m.Preview == null || IsSafePath(m.Preview))
            && m.Players >= 0 && m.SizeBytes >= 0;

        // The name becomes a folder in the user's Maps folder
        private static bool IsSafeName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 150 && name == name.Trim() && !name.EndsWith('.')
            && name != "." && name != ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        private static bool IsSafePath(string? path) =>
            path != null && SafePath.IsMatch(path) && !path.Split('/').Any(part => part == "." || part == "..");

        // A file of a folder library; null when it would land outside the folder
        private string? LocalFile(string relative)
        {
            string root = _folder!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        // ── Previews ──

        // The preview picture as a file on this PC: from the library folder, or downloaded once into the cache
        public async Task<string?> PreviewFileAsync(LibraryMap map, CancellationToken ct = default)
        {
            if (map.Preview == null)
                return null;
            if (_folder != null)
                return LocalFile(map.Preview) is { } local && File.Exists(local) ? local : null;

            string file = Path.Combine(CacheFolder, "previews", map.Id + ".png");
            if (File.Exists(file))
                return file;
            if (_webDown || _noPreview.ContainsKey(map.Id))
                return null;

            string temp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                using var response = await GoApi.Http.GetAsync(new Uri(_web!, map.Preview), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                EnsureSuccess(response);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await CopyAsync(stream, output, PreviewLimit, null, null, ct).ConfigureAwait(false);
                File.Move(temp, file, overwrite: true);
                return file;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                // Without a connection every other preview would wait for the same timeout
                if (ex is HttpRequestException { StatusCode: null } or TimeoutException or TaskCanceledException && !ct.IsCancellationRequested)
                    _webDown = true;
                _noPreview[map.Id] = true;
                return null;
            }
        }

        // ── Download and install ──

        // Downloads (or copies) one map's zip to a temporary file and checks it. The caller deletes the file.
        public async Task<string> DownloadAsync(LibraryMap map, Action<long>? progress = null, CancellationToken ct = default)
        {
            string temp = Path.Combine(Path.GetTempPath(), "CommandCenter-map-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long limit = map.SizeBytes > 0 ? map.SizeBytes : ZipLimit;
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    if (_web != null)
                    {
                        using var response = await GoApi.Http.GetAsync(new Uri(_web, map.Zip), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        EnsureSuccess(response);
                        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        await CopyAsync(stream, output, limit, hash, progress, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        string source = LocalFile(map.Zip) is { } local && File.Exists(local)
                            ? local
                            : throw new FileNotFoundException($"{map.Zip} is missing from the library folder.");
                        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                        await CopyAsync(stream, output, limit, hash, progress, ct).ConfigureAwait(false);
                    }
                }

                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(map.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download does not match the library's checksum, so it was not installed.");
                return temp;
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        // Installs the maps one after another. Maps whose folder already exists are skipped, so nothing the user has is
        // overwritten. Progress reports where the run is, for one line of text and a progress bar.
        public async Task<InstallReport> InstallAsync(IReadOnlyList<LibraryMap> maps, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        {
            var installed = new List<string>();
            var failed = new List<string>();
            int skipped = 0;
            long total = Math.Max(1, maps.Sum(m => m.SizeBytes)), done = 0;

            for (int i = 0; i < maps.Count; i++)
            {
                var map = maps[i];
                if (ct.IsCancellationRequested)
                    return new InstallReport(installed, skipped, failed, true);
                if (IsInstalled(map))
                {
                    skipped++;
                    done += map.SizeBytes;
                    continue;
                }

                int shown = -1, number = i + 1;
                void Report(long bytes)
                {
                    int percent = maps.Count > 1
                        ? (int)Math.Min(100, (done + bytes) * 100 / total)
                        : (int)Math.Min(100, bytes * 100 / Math.Max(1, map.SizeBytes));
                    if (percent == shown)
                        return;
                    shown = percent;
                    progress?.Report(new InstallProgress(number, maps.Count, map.Name, percent, false));
                }
                Report(0);

                string? zip = null;
                try
                {
                    zip = await DownloadAsync(map, Report, ct).ConfigureAwait(false);
                    progress?.Report(new InstallProgress(number, maps.Count, map.Name, Math.Max(shown, 0), true));
                    string verified = zip;
                    var names = await Task.Run(() => MapService.Install(verified), CancellationToken.None).ConfigureAwait(false);
                    if (names.Count == 0)
                        failed.Add($"{map.Name}: the download holds no map");
                    else
                        installed.AddRange(names);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new InstallReport(installed, skipped, failed, true);
                }
                catch (Exception ex)
                {
                    failed.Add($"{map.Name}: {Reason(ex)}");
                }
                finally
                {
                    if (zip != null)
                        TryDelete(zip);
                    done += map.SizeBytes;
                }
            }
            return new InstallReport(installed, skipped, failed, false);
        }

        // ── Helpers ──

        private static void EnsureSuccess(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        // Copies with a size limit, and gives up when the other side stops sending
        private static async Task CopyAsync(Stream input, Stream output, long limit, IncrementalHash? hash, Action<long>? progress, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                int read;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    stall.CancelAfter(StallTimeout);
                    try { read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The server stopped sending data."); }
                }
                if (read == 0)
                    return;
                total += read;
                if (total > limit)
                    throw new InvalidDataException("The file is bigger than the library says, so the download was stopped.");
                hash?.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Invoke(total);
            }
        }

        // A short sentence for the user
        public static string Reason(Exception ex) => ex switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "The server answered \"not found\" (404).",
            HttpRequestException { StatusCode: not null } => ex.Message,
            HttpRequestException => "No connection to the server. Check your internet connection.",
            TaskCanceledException => "The server took too long to answer.",
            JsonException => "The catalog is damaged or is not a map library catalog.",
            _ => ex.Message,
        };

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
