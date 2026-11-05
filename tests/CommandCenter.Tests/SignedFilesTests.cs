using System.Net;
using System.Net.Http;
using System.Text;

namespace CommandCenter.Tests
{
    // The GO profile and the online map catalog are used only with the maintainer's signature (seam 1, ticket 09).
    // The files are served by an HTTP handler made in the test and signed with the test key.
    public sealed class SignedFilesTests : IDisposable
    {
        private const string Profile = """
            {
              "schema": 1,
              "liveExe": "GeneralsOnlineZH_62.exe",
              "testExe": "GeneralsOnlineZH_TestEnvironment.exe",
              "eacLauncherExe": "EAC_LaunchGeneralsOnline.exe",
              "officialLauncherExe": "GeneralsOnlineZH.exe",
              "eacPlugin": "easyanticheat",
              "channelKey": "prefer_experiemental_client",
              "communityPatchFile": "500_900_CommunityPatch_CoreINI.big",
              "apiBase": "https://api.playgenerals.online/env/prod/contract/1/",
              "manifestKey": "execrc_62"
            }
            """;

        private const string Catalog = """
            {"schemaVersion":1,"generated":"2026-11-05T00:00:00Z","maps":[{"id":"alpha","name":"Alpha","players":2,"sizeBytes":10,
             "sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","file":"maps/alpha.zip"}]}
            """;

        private const string BuiltInLiveExe = "GeneralsOnlineZH_60.exe";
        private static readonly string Online = MapLibraryClient.OnlineLibrary;

        private readonly TempGame _game = new();
        private readonly IDisposable _trust = TestKeys.Trust();
        private readonly Dictionary<string, byte[]> _served = new();

        public SignedFilesTests()
        {
            DownloadService.TestHandler = new Server(_served);
            GoProfile.FileOverride = null;
            GoProfile.Reset();
        }

        // Answers what the test serves, and 404 for anything else
        private sealed class Server(Dictionary<string, byte[]> served) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(served.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        private void ServeSigned(string url, string text, SignedKind kind, string? signedText = null)
        {
            _served[url] = Bytes(text);
            _served[url + ".sig"] = Bytes(TestKeys.Sign(kind, Bytes(signedText ?? text)));
        }

        // ── GO profile ──

        [Fact]
        public async Task A_signed_go_profile_is_used_and_still_used_from_the_cache_at_the_next_start()
        {
            ServeSigned(GoProfile.RemoteUrl, Profile, SignedKind.GoProfile);
            await GoProfile.RefreshAsync();
            Assert.Equal("GeneralsOnlineZH_62.exe", GoProfile.Current.LiveExe);

            _served.Clear();
            GoProfile.Reset();
            Assert.Equal("GeneralsOnlineZH_62.exe", GoProfile.Current.LiveExe);
        }

        [Fact]
        public async Task A_changed_go_profile_is_refused_and_not_cached()
        {
            ServeSigned(GoProfile.RemoteUrl, Profile.Replace("_62.exe", "_63.exe"), SignedKind.GoProfile, signedText: Profile);
            await GoProfile.RefreshAsync();
            Assert.Equal(BuiltInLiveExe, GoProfile.Current.LiveExe);
            GoProfile.Reset();
            Assert.Equal(BuiltInLiveExe, GoProfile.Current.LiveExe);
        }

        [Fact]
        public async Task A_go_profile_without_a_signature_is_refused()
        {
            _served[GoProfile.RemoteUrl] = Bytes(Profile);
            await GoProfile.RefreshAsync();
            Assert.Equal(BuiltInLiveExe, GoProfile.Current.LiveExe);
        }

        [Fact]
        public async Task A_go_profile_signed_as_another_kind_is_refused()
        {
            ServeSigned(GoProfile.RemoteUrl, Profile, SignedKind.MapCatalog);
            await GoProfile.RefreshAsync();
            Assert.Equal(BuiltInLiveExe, GoProfile.Current.LiveExe);
        }

        [Fact]
        public async Task A_cached_go_profile_that_was_changed_falls_back_to_the_built_in_one()
        {
            ServeSigned(GoProfile.RemoteUrl, Profile, SignedKind.GoProfile);
            await GoProfile.RefreshAsync();
            string cache = Path.Combine(GamePaths.AppData, "cache", "go-profile.json");
            File.WriteAllText(cache, Profile.Replace("_62.exe", "_63.exe"));
            GoProfile.Reset();
            Assert.Equal(BuiltInLiveExe, GoProfile.Current.LiveExe);
        }

        [Fact]
        public void A_go_profile_file_given_at_startup_needs_its_signature()
        {
            string file = _game.Write("profile.json", Profile);
            GoProfile.FileOverride = file;
            GoProfile.Reset();
            Assert.Equal(BuiltInLiveExe, GoProfile.Current.LiveExe);

            TestKeys.SignFile(file, SignedKind.GoProfile);
            GoProfile.Reset();
            Assert.Equal("GeneralsOnlineZH_62.exe", GoProfile.Current.LiveExe);
        }

        // ── Online map catalog ──

        [Fact]
        public async Task A_signed_online_catalog_is_read_and_read_again_from_the_cache()
        {
            ServeSigned(Online + "catalog.json", Catalog, SignedKind.MapCatalog);
            var result = await new MapLibraryClient(Online).LoadCatalogAsync(refresh: true);
            Assert.Null(result.Problem);
            Assert.Single(result.Catalog!.Maps!);

            _served.Clear();
            var cached = await new MapLibraryClient(Online).LoadCatalogAsync();
            Assert.Null(cached.Problem);
            Assert.Single(cached.Catalog!.Maps!);
        }

        [Fact]
        public async Task A_changed_online_catalog_is_refused_and_says_why()
        {
            ServeSigned(Online + "catalog.json", Catalog.Replace("Alpha", "Bravo"), SignedKind.MapCatalog, signedText: Catalog);
            var result = await new MapLibraryClient(Online).LoadCatalogAsync(refresh: true);
            Assert.Null(result.Catalog);
            Assert.Equal("The online map library can't be read", result.Problem);
            Assert.Contains("signature", result.Detail);
        }

        [Fact]
        public async Task An_online_catalog_without_a_signature_is_refused()
        {
            _served[Online + "catalog.json"] = Bytes(Catalog);
            var result = await new MapLibraryClient(Online).LoadCatalogAsync(refresh: true);
            Assert.Null(result.Catalog);
            Assert.Contains("signature", result.Detail);
        }

        [Fact]
        public async Task A_refused_catalog_falls_back_to_the_last_verified_copy()
        {
            ServeSigned(Online + "catalog.json", Catalog, SignedKind.MapCatalog);
            Assert.NotNull((await new MapLibraryClient(Online).LoadCatalogAsync(refresh: true)).Catalog);

            ServeSigned(Online + "catalog.json", Catalog.Replace("Alpha", "Bravo"), SignedKind.MapCatalog, signedText: Catalog);
            var result = await new MapLibraryClient(Online).LoadCatalogAsync(refresh: true);
            Assert.Equal("Alpha", Assert.Single(result.Catalog!.Maps!).Name);
            Assert.NotNull(result.Problem);
        }

        [Fact]
        public async Task An_unsigned_catalog_cached_by_an_older_version_is_not_used()
        {
            var client = new MapLibraryClient(Online);
            ServeSigned(Online + "catalog.json", Catalog, SignedKind.MapCatalog);
            await client.LoadCatalogAsync(refresh: true);
            string cached = Directory.EnumerateFiles(GamePaths.AppData, "catalog.json", SearchOption.AllDirectories).Single();
            File.Delete(cached + ".sig");
            _served.Clear();

            var result = await new MapLibraryClient(Online).LoadCatalogAsync();
            Assert.Null(result.Catalog);
        }

        [Fact]
        public async Task A_library_folder_needs_no_signature()
        {
            string folder = Path.GetDirectoryName(_game.Write(@"library\catalog.json", Catalog))!;
            var result = await new MapLibraryClient(folder).LoadCatalogAsync();
            Assert.Null(result.Problem);
            Assert.Single(result.Catalog!.Maps!);
        }

        public void Dispose()
        {
            DownloadService.TestHandler = null;
            GoProfile.Reset();
            _trust.Dispose();
            _game.Dispose();
        }
    }
}
