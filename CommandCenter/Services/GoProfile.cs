using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommandCenter.Services
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

        // --go-profile <file> uses a local file instead (for tests); otherwise the last profile fetched, else the
        // built-in values
        private static GoProfile LoadLocal()
        {
            string? path = App.Arg("--go-profile") ?? (File.Exists(CachePath) ? CachePath : null);
            try
            {
                if (path != null && Parse(File.ReadAllText(path)) is { } profile)
                    return profile;
            }
            catch { }
            return new GoProfile();
        }

        // Fetches the current profile in the background; an offline or broken answer keeps the one in use
        public static async Task RefreshAsync()
        {
            if (App.Arg("--go-profile") != null)
                return;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CommandCenter/" + UpdateService.Current.ToString(3));
                string json = await http.GetStringAsync(RemoteUrl);
                if (Parse(json) is not { } profile)
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, json);
                Current = profile;
                Changed?.Invoke();
            }
            catch
            {
                // Offline: the cached or built-in profile stays
            }
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
