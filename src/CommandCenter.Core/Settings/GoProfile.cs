using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommandCenter.Core.Settings
{
    // Everything Command Center needs to know by name about Generals Online: its programs, the anti-cheat plugin, the
    // launcher.json key for the channel and the service address. The values here match Generals Online as of October
    // 2026. When Generals Online renames something, go-profile.json in Command Center's repository is updated and every
    // copy picks it up on its next start, without waiting for a new Command Center.
    public sealed class GoProfile
    {
        public const int SupportedSchema = 1;
        public const string RemoteUrl = "https://raw.githubusercontent.com/MAWKLY-VK/command-center/main/go-profile.json";

        public int Schema { get; init; } = SupportedSchema;

        // The live client, the test client, the Easy Anti-Cheat launcher and the official launcher, in the game folder
        public string LiveExe { get; init; } = "GeneralsOnlineZH_60.exe";
        public string TestExe { get; init; } = "GeneralsOnlineZH_TestEnvironment.exe";
        public string EacLauncherExe { get; init; } = "EAC_LaunchGeneralsOnline.exe";
        public string OfficialLauncherExe { get; init; } = "GeneralsOnlineZH.exe";

        // settings.json plugins.anticheat value for Easy Anti-Cheat, and which channels the official launcher starts
        // through the Easy Anti-Cheat launcher when it is selected (since 3 October 2026 only the test client)
        public string EacPlugin { get; init; } = "easyanticheat";
        public bool LiveThroughEac { get; init; }
        public bool TestThroughEac { get; init; } = true;

        // launcher.json key for the channel (misspelt by Generals Online; true means the live client)
        public string ChannelKey { get; init; } = "prefer_experiemental_client";

        // The community data patch archive Generals Online keeps in Documents\...\GeneralsOnlineGameData. When it is
        // missing every normal room shows greyed out (Generals Online support's first answer for that)
        public string CommunityPatchFile { get; init; } = "500_900_CommunityPatch_CoreINI.big";

        // The public Generals Online service and the VersionManifest field for the live client's fingerprint
        public string ApiBase { get; init; } = "https://api.playgenerals.online/env/prod/contract/1/";
        public string ManifestKey { get; init; } = "execrc_60";

        private static GoProfile? _current;

        public static GoProfile Current
        {
            get => _current ??= LoadLocal();
            private set => _current = value;
        }

        public static event Action? Changed;

        private static string CachePath => Path.Combine(GamePaths.AppData, "cache", "go-profile.json");

        // --go-profile <file>: a local file used instead of the fetched profile (for tests and screenshots). Like every
        // profile it counts only with its signature next to it (<file>.sig). AppServices sets it at startup, before
        // anything reads Current.
        public static string? FileOverride { get; set; }

        // Forgets the profile in use, so the next read of Current loads it again (for tests)
        internal static void Reset() => _current = null;

        // The --go-profile file, otherwise the last profile fetched, else the built-in values. A file counts only with
        // the maintainer's signature (ADR 0007), so a profile Command Center did not verify never names a program.
        private static GoProfile LoadLocal()
        {
            string? path = FileOverride ?? (File.Exists(CachePath) ? CachePath : null);
            try
            {
                string? signature = path != null && File.Exists(path + ".sig") ? File.ReadAllText(path + ".sig") : null;
                if (path != null && Verified(File.ReadAllBytes(path), signature, path) is { } profile)
                    return profile;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new GoProfile();
        }

        // Fetches the current profile and its signature in the background; an offline, unsigned or broken answer keeps
        // the one in use
        public static async Task RefreshAsync()
        {
            if (FileOverride != null)
                return;
            try
            {
                var wait = TimeSpan.FromSeconds(8);
                byte[] json = await DownloadService.GetBytesAsync(new Uri(RemoteUrl), DownloadService.SmallFileLimit, wait);
                byte[]? signature;
                try
                {
                    signature = await DownloadService.GetBytesAsync(new Uri(RemoteUrl + ".sig"), DownloadService.SmallFileLimit, wait);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Not published: refused below, and the operation log says so
                    signature = null;
                }
                if (Verified(json, signature == null ? null : Encoding.ASCII.GetString(signature), RemoteUrl) is not { } profile)
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllBytesAsync(CachePath, json);
                await File.WriteAllBytesAsync(CachePath + ".sig", signature!);
                Current = profile;
                Changed?.Invoke();
            }
            catch
            {
                // Offline, or no signature published: the cached or built-in profile stays
            }
        }

        // The profile in these bytes when the maintainer signed them and they pass Parse; otherwise null, and the
        // operation log says why
        private static GoProfile? Verified(byte[] json, string? signature, string source)
        {
            if (!ManifestVerifier.Verify(SignedKind.GoProfile, json, signature, out string reason))
            {
                OperationLog.Failed("verify GO profile", source, reason);
                return null;
            }
            var text = json.AsSpan();
            if (text.StartsWith(Encoding.UTF8.Preamble))
                text = text[Encoding.UTF8.Preamble.Length..];
            return Parse(Encoding.UTF8.GetString(text));
        }

        private static readonly Regex ExeName = new(@"^[A-Za-z0-9 _.\-]{1,64}\.exe$", RegexOptions.IgnoreCase);
        private static readonly Regex ArchiveName = new(@"^[A-Za-z0-9 _.\-]{1,80}\.big$", RegexOptions.IgnoreCase);
        private static readonly Regex Word = new(@"^[A-Za-z0-9_\-]{1,64}$");

        // Only plain file names in the game folder and only Generals Online's own service are accepted, so a profile
        // can rename what is started but can never point Command Center at another folder or site
        public static GoProfile? Parse(string json)
        {
            try
            {
                var profile = JsonSerializer.Deserialize<GoProfile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (profile == null || profile.Schema != SupportedSchema)
                    return null;
                bool exes = new[] { profile.LiveExe, profile.TestExe, profile.EacLauncherExe, profile.OfficialLauncherExe }.All(n => n != null && ExeName.IsMatch(n));
                bool words = new[] { profile.EacPlugin, profile.ChannelKey, profile.ManifestKey }.All(w => w != null && Word.IsMatch(w))
                             && profile.CommunityPatchFile != null && ArchiveName.IsMatch(profile.CommunityPatchFile);
                bool api = Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var uri)
                           && uri.Scheme == Uri.UriSchemeHttps
                           && (uri.Host.Equals("playgenerals.online", StringComparison.OrdinalIgnoreCase)
                               || uri.Host.EndsWith(".playgenerals.online", StringComparison.OrdinalIgnoreCase))
                           && profile.ApiBase.EndsWith('/');
                return exes && words && api ? profile : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
