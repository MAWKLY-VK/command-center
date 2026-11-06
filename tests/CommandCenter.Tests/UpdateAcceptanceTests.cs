using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CommandCenter.Tests
{
    // An update is offered only when its signed payload passes every rule, and the program it downloads must carry the
    // version it names (seam 1, ticket 10). Manifests are signed with the test key.
    public sealed class UpdateAcceptanceTests : IDisposable
    {
        private const string Releases = "https://github.com/MAWKLY-VK/command-center/releases/download/";
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private readonly TempGame _game = new();
        private readonly IDisposable _trust = TestKeys.Trust();

        private static Version Newer => new(UpdateService.Current.Major, UpdateService.Current.Minor + 1, 0);

        private static JsonObject Payload(Version? version = null, string? url = null, string sha = Hash, DateTime? expires = null) => new()
        {
            ["version"] = (version ?? Newer).ToString(3),
            ["url"] = url ?? Releases + "v9.9.9/CommandCenter.exe",
            ["sha256"] = sha,
            ["notes"] = "What changed",
            ["expires"] = (expires ?? DateTime.UtcNow.AddDays(30)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        };

        // A manifest with the old fields and the signed payload; tamper changes the payload after it was signed
        private string Manifest(JsonObject payload, SignedKind kind = SignedKind.Update, Func<string, string>? tamper = null, bool sign = true)
        {
            string text = payload.ToJsonString();
            var manifest = new JsonObject
            {
                ["version"] = payload["version"]?.GetValue<string>(),
                ["url"] = payload["url"]?.GetValue<string>(),
                ["sha256"] = payload["sha256"]?.GetValue<string>(),
                ["notes"] = "What changed",
            };
            if (sign)
            {
                manifest["signed"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(tamper?.Invoke(text) ?? text));
                manifest["signature"] = TestKeys.Sign(kind, Encoding.UTF8.GetBytes(text));
            }
            return _game.Write("update-" + Guid.NewGuid().ToString("N") + ".json", manifest.ToJsonString());
        }

        private static string Log => File.ReadAllText(Path.Combine(GamePaths.AppData, "operations.log"));

        // Settings' "Check for updates" tells the three answers apart (ticket 08)
        [Fact]
        public async Task A_check_that_cannot_run_says_why_instead_of_saying_the_version_is_the_latest()
        {
            var unreachable = await UpdateService.CheckNowAsync(Path.Combine(_game.Root, "no-such-manifest.json"));
            Assert.Null(unreachable.Update);
            Assert.Equal("The update information could not be read. Check the internet connection.", unreachable.Failure);

            var refused = await UpdateService.CheckNowAsync(Manifest(Payload(), sign: false));
            Assert.Null(refused.Update);
            Assert.StartsWith("The update information was refused: ", refused.Failure);

            var latest = await UpdateService.CheckNowAsync(Manifest(Payload(version: UpdateService.Current)));
            Assert.Equal((null, null), (latest.Update, latest.Failure));

            Assert.NotNull((await UpdateService.CheckNowAsync(Manifest(Payload()))).Update);
        }

        [Fact]
        public async Task A_signed_manifest_that_passes_every_rule_offers_the_update()
        {
            var update = await UpdateService.CheckAsync(Manifest(Payload()));
            Assert.NotNull(update);
            Assert.Equal(Newer.ToString(3), update!.Version.ToString(3));
            Assert.Equal(Releases + "v9.9.9/CommandCenter.exe", update.Url);
            Assert.Equal(Hash, update.Sha256);
            Assert.Equal("What changed", update.Notes);
        }

        [Fact]
        public async Task A_payload_changed_after_signing_is_refused_and_logged()
        {
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(), tamper: t => t.Replace("What changed", "Something else"))));
            Assert.Contains("signature", Log);
        }

        [Fact]
        public async Task A_payload_signed_as_another_kind_is_refused() =>
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(), SignedKind.GoProfile)));

        [Fact]
        public async Task A_manifest_with_only_the_old_fields_is_refused_and_logged()
        {
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(), sign: false)));
            Assert.Contains("signature", Log);
        }

        [Fact]
        public async Task The_same_or_an_older_version_is_not_offered()
        {
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(version: UpdateService.Current))));
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(version: new Version(0, 1, 0)))));
        }

        [Fact]
        public async Task An_expired_manifest_is_refused_and_logged()
        {
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(expires: DateTime.UtcNow.AddDays(-1)))));
            Assert.Contains("expired", Log);
        }

        [Fact]
        public async Task A_download_outside_the_release_downloads_is_refused_and_logged()
        {
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(url: "https://example.org/CommandCenter.exe"))));
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(url: "https://github.com/someone/else/releases/download/v1/CommandCenter.exe"))));
            Assert.Contains("release", Log);
        }

        [Fact]
        public async Task A_bad_hash_is_refused_and_logged()
        {
            Assert.Null(await UpdateService.CheckAsync(Manifest(Payload(sha: "abc"))));
            Assert.Contains("SHA-256", Log);
        }

        [Fact]
        public async Task A_payload_missing_a_field_is_refused_and_logged()
        {
            var payload = Payload();
            payload.Remove("expires");
            Assert.Null(await UpdateService.CheckAsync(Manifest(payload)));
            Assert.Contains("expires", Log);
        }

        [Fact]
        public async Task A_local_download_is_offered_only_by_a_test_build()
        {
            string local = Manifest(Payload(url: @"C:\Builds\CommandCenter.exe"));
            Assert.Null(await UpdateService.CheckAsync(local));
            UpdateService.AllowLocalDownloads = true;
            Assert.NotNull(await UpdateService.CheckAsync(local));
        }

        [Fact]
        public async Task A_downloaded_program_must_carry_the_manifests_version()
        {
            UpdateService.AllowLocalDownloads = true;
            string program = typeof(App).Assembly.Location;
            var info = FileVersionInfo.GetVersionInfo(program);
            var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, 0);
            string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(program))).ToLowerInvariant();
            var progress = new Progress<(long Done, long Total)>();

            var right = new UpdateInfo(version, version.ToString(3), program, sha, "");
            string downloaded = await UpdateService.DownloadAsync(right, progress);
            File.Delete(downloaded);

            var other = new Version(version.Major, version.Minor, version.Build + 1, 0);
            var wrong = right with { Version = other, VersionText = other.ToString(3) };
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.DownloadAsync(wrong, progress));
            Assert.Contains(version.ToString(3), error.Message);
        }

        public void Dispose()
        {
            UpdateService.AllowLocalDownloads = false;
            _trust.Dispose();
            _game.Dispose();
        }
    }
}
