using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace CommandCenter.Tests
{
    // Downloads through an HTTP handler made in the test (seam 2): limits, stalls, checksums and the temporary file
    public sealed class DownloadServiceTests : IDisposable
    {
        private static readonly Uri Url = new("https://example.org/file.bin");
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N"));
        private readonly TimeSpan _stall = DownloadService.StallTimeout;

        public DownloadServiceTests()
        {
            Directory.CreateDirectory(_temp);
            DownloadService.TempFolder = _temp;
            DownloadService.StallTimeout = TimeSpan.FromMilliseconds(300);
        }

        private sealed class Server(Func<HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer());
        }

        // A body that sends its bytes and then stops, never ending
        private sealed class StallingStream(byte[] first) : Stream
        {
            private bool _sent;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (!_sent)
                {
                    _sent = true;
                    first.CopyTo(buffer);
                    return first.Length;
                }
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // A body that must never be read
        private sealed class UnreadableStream : MemoryStream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new InvalidOperationException("read");
            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("read");
        }

        private void Serve(byte[] body, long? announced = null) => DownloadService.TestHandler = new Server(() =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = announced;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        private bool TempIsEmpty => !Directory.EnumerateFileSystemEntries(_temp).Any();

        [Fact]
        public async Task A_file_within_its_limit_and_checksum_arrives_in_a_private_temporary_file()
        {
            byte[] body = RandomNumberGenerator.GetBytes(1000);
            Serve(body);
            string path = await DownloadService.DownloadFileAsync(Url, 1000, Sha(body));
            Assert.Equal(_temp, Path.GetDirectoryName(path));
            Assert.Equal(body, File.ReadAllBytes(path));
        }

        [Fact]
        public async Task A_file_over_its_limit_is_stopped_and_removed()
        {
            Serve(new byte[1001]);
            await Assert.ThrowsAsync<DownloadTooLargeException>(() => DownloadService.DownloadFileAsync(Url, 1000));
            Assert.True(TempIsEmpty);
        }

        [Fact]
        public async Task A_server_that_announces_less_than_it_sends_is_still_stopped_at_the_limit()
        {
            Serve(new byte[5000], announced: 10);
            await Assert.ThrowsAsync<DownloadTooLargeException>(() => DownloadService.DownloadFileAsync(Url, 1000));
            Assert.True(TempIsEmpty);
        }

        [Fact]
        public async Task An_announced_size_over_the_limit_is_refused_before_anything_is_read()
        {
            DownloadService.TestHandler = new Server(() =>
            {
                var content = new StreamContent(new UnreadableStream());
                content.Headers.ContentLength = 2000;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });
            await Assert.ThrowsAsync<DownloadTooLargeException>(() => DownloadService.DownloadFileAsync(Url, 1000));
            Assert.True(TempIsEmpty);
        }

        [Fact]
        public async Task A_stall_stops_the_download_and_removes_the_file()
        {
            DownloadService.TestHandler = new Server(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(new byte[100])) });
            await Assert.ThrowsAsync<TimeoutException>(() => DownloadService.DownloadFileAsync(Url, 1000));
            Assert.True(TempIsEmpty);
        }

        [Fact]
        public async Task A_checksum_mismatch_fails_and_removes_the_file()
        {
            Serve(new byte[100]);
            await Assert.ThrowsAsync<ChecksumMismatchException>(() => DownloadService.DownloadFileAsync(Url, 1000, Sha(new byte[99])));
            Assert.True(TempIsEmpty);
        }

        [Fact]
        public async Task A_server_error_fails_with_its_status()
        {
            DownloadService.TestHandler = new Server(() => new HttpResponseMessage(HttpStatusCode.NotFound));
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => DownloadService.GetBytesAsync(Url, 1000));
            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        }

        [Fact]
        public async Task Small_files_are_read_into_memory_within_their_limit()
        {
            byte[] body = RandomNumberGenerator.GetBytes(500);
            Serve(body);
            Assert.Equal(body, await DownloadService.GetBytesAsync(Url, 500));
            Serve(new byte[501]);
            await Assert.ThrowsAsync<DownloadTooLargeException>(() => DownloadService.GetBytesAsync(Url, 500));
        }

        [Fact]
        public void The_shared_client_follows_the_windows_proxy()
        {
            using var handler = DownloadService.CreateHandler();
            Assert.True(handler.UseProxy);
            Assert.Null(handler.Proxy);
            // A proxy that asks who is connecting gets the Windows user's own sign-in
            Assert.Same(System.Net.CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
        }

        public void Dispose()
        {
            DownloadService.TestHandler = null;
            DownloadService.TempFolder = null;
            DownloadService.StallTimeout = _stall;
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        }
    }
}
