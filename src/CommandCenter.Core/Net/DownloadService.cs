using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace CommandCenter.Core.Net
{
    // A download bigger than its limit: it is stopped before more is written
    public sealed class DownloadTooLargeException(string message) : IOException(message);

    // A download whose SHA-256 is not the expected one
    public sealed class ChecksumMismatchException(string message) : IOException(message);

    // The one way Command Center downloads (spec phase 3, P3-D2): one client that follows the Windows proxy, a size limit
    // for each kind of file, a stop when the server stops sending, the SHA-256 checked while the file streams in, and a
    // private temporary file that is removed on any failure. The network diagnostics keep their own direct connections,
    // because they measure the route the game itself uses.
    public static class DownloadService
    {
        public const long UpdateProgramLimit = 64L * 1024 * 1024;
        public const long CatalogLimit = 32L * 1024 * 1024;
        public const long PreviewLimit = 4L * 1024 * 1024;
        public const long MapZipLimit = 256L * 1024 * 1024;

        // update.json, the GO profile, signature files, and the GO stats and version manifest
        public const long SmallFileLimit = 1024 * 1024;

        // Waiting longer than this for the reply headers or for the next bytes stops a download
        internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

        // The tests answer the requests themselves, and keep their temporary files in their own folder
        internal static HttpMessageHandler? TestHandler { get; set; }
        internal static string? TempFolder { get; set; }

        private static readonly HttpClient Shared = CreateClient(CreateHandler());

        // Follows the Windows proxy setting (UseProxy with no proxy of its own); without a proxy it connects IPv4 first
        internal static SocketsHttpHandler CreateHandler() => new() { UseProxy = true, ConnectCallback = ConnectAsync };

        private static HttpClient CreateClient(HttpMessageHandler handler, bool disposeHandler = true)
        {
            // No overall timeout: big files take minutes, so each wait has its own limit instead
            var client = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CommandCenter/" + UpdateService.Current.ToString(3));
            return client;
        }

        private static HttpClient Client => TestHandler is { } handler ? CreateClient(handler, disposeHandler: false) : Shared;

        // Sends a request and returns the response once its headers arrived; the caller disposes it
        public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout ?? StallTimeout);
            try
            {
                return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The server took too long to answer.");
            }
        }

        // A small file, read into memory: at most limit bytes
        public static async Task<byte[]> GetBytesAsync(Uri url, long limit, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendAsync(request, timeout, ct).ConfigureAwait(false);
            EnsureSuccess(response);
            return await ReadAsync(response, limit, ct).ConfigureAwait(false);
        }

        // The body of a response that already arrived, at most limit bytes
        public static async Task<byte[]> ReadAsync(HttpResponseMessage response, long limit, CancellationToken ct = default)
        {
            CheckAnnounced(response, limit);
            using var buffer = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await CopyAsync(stream, buffer, limit, null, null, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }

        // Downloads to a new private temporary file and checks the SHA-256 when it is given. Progress gets the bytes so
        // far and the announced size (-1 when unknown). Returns the file's path; the caller deletes it. On any failure
        // the file is removed.
        public static async Task<string> DownloadFileAsync(Uri url, long limit, string? sha256 = null, Action<long, long>? progress = null,
            CancellationToken ct = default, string extension = ".download")
        {
            string? temp = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendAsync(request, null, ct).ConfigureAwait(false);
                EnsureSuccess(response);
                CheckAnnounced(response, limit);
                using var hash = sha256 != null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                temp = NewTempPath(extension);
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    long total = response.Content.Headers.ContentLength ?? -1;
                    await CopyAsync(stream, output, limit, hash, progress == null ? null : done => progress(done, total), ct).ConfigureAwait(false);
                }
                if (hash != null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ChecksumMismatchException("The download does not match its checksum.");
                return temp;
            }
            catch
            {
                if (temp != null)
                    TryDelete(temp);
                throw;
            }
        }

        // A name nobody can guess, in the temporary folder; the file is created with FileMode.CreateNew by the caller
        internal static string NewTempPath(string extension) =>
            Path.Combine(TempFolder ?? Path.GetTempPath(), "CommandCenter-" + Guid.NewGuid().ToString("N") + extension);

        // Copies with a size limit, and gives up when the other side stops sending
        internal static async Task CopyAsync(Stream input, Stream output, long limit, IncrementalHash? hash, Action<long>? progress, CancellationToken ct)
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
                    throw new DownloadTooLargeException("The download is bigger than expected, so it was stopped.");
                hash?.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Invoke(total);
            }
        }

        internal static void EnsureSuccess(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        private static void CheckAnnounced(HttpResponseMessage response, long limit)
        {
            if (response.Content.Headers.ContentLength is long length && length > limit)
                throw new DownloadTooLargeException("The download is bigger than expected, so it was stopped.");
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // Tries IPv4 addresses before IPv6 and gives each one a few seconds, so a home network with a broken IPv6 route
        // answers quickly instead of waiting for the system's long connect timeout. With a proxy, this reaches the proxy.
        private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false))
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ToList();
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    socket.Dispose();
                    last = ex;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            throw last ?? new SocketException((int)SocketError.HostNotFound);
        }
    }
}
