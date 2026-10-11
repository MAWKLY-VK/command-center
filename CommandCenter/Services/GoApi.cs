using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandCenter.Services
{
    public sealed record ServerStats(int Players, int Lobbies, int LatencyMs);

    // Public Generals Online status endpoint, the same one the official launcher shows on its home page.
    public static class GoApi
    {
        private static string StatsUrl => GoProfile.Current.ApiBase + "Monitoring/BasicStats";

        // The fingerprints of the current official clients, published by Generals Online for its own update check
        private static string ManifestUrl => GoProfile.Current.ApiBase + "VersionManifest";

        public static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, ConnectCallback = ConnectAsync })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        public static async Task<ServerStats?> GetStatsAsync()
        {
            try
            {
                // The first request pays for DNS and the TLS handshake; the second one shows the real response time
                string json = await Http.GetStringAsync(StatsUrl);
                var watch = Stopwatch.StartNew();
                try { json = await Http.GetStringAsync(StatsUrl); } catch { }
                watch.Stop();
                using var doc = JsonDocument.Parse(json);
                int players = doc.RootElement.TryGetProperty("players", out var p) ? p.GetInt32() : 0;
                int lobbies = doc.RootElement.TryGetProperty("lobbies", out var l) ? l.GetInt32() : 0;
                return new ServerStats(players, lobbies, (int)watch.ElapsedMilliseconds);
            }
            catch
            {
                return null;
            }
        }

        // The fingerprint (CRC-32 of the whole file) the current live client must have; null when it cannot be read
        public static async Task<uint?> GetLiveFingerprintAsync()
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(ManifestUrl));
                return doc.RootElement.TryGetProperty(GoProfile.Current.ManifestKey, out var value) && value.TryGetUInt32(out uint crc)
                    ? crc
                    : null;
            }
            catch
            {
                return null;
            }
        }

        // Tries IPv4 addresses before IPv6 and gives each one a few seconds, so a home network with a broken
        // IPv6 route answers quickly instead of waiting for the system's long connect timeout.
        private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct))
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
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token);
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

    // Generals Online's settings.json, edited as a JSON tree so fields this launcher does not know are kept.
    public sealed class GoSettings
    {
        public JsonObject Root { get; private set; } = new();

        public static GoSettings Load()
        {
            try
            {
                if (File.Exists(GamePaths.GoSettings))
                    return Parse(File.ReadAllText(GamePaths.GoSettings));
            }
            catch
            {
                // An unreadable file is reported by the health check
            }
            return new GoSettings();
        }

        // A broken file gives empty settings; the health check reports it
        internal static GoSettings Parse(string json)
        {
            var settings = new GoSettings();
            try
            {
                if (JsonNode.Parse(json) is JsonObject root)
                    settings.Root = root;
            }
            catch { }
            return settings;
        }

        internal byte[] ToBytes() => System.Text.Encoding.UTF8.GetBytes(Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        public T Get<T>(string section, string key, T fallback)
        {
            try
            {
                var node = Root[section]?[key];
                return node != null ? node.GetValue<T>() : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public void Set<T>(string section, string key, T value) => SetNode(section, key, JsonValue.Create(value));

        public void SetNode(string section, string key, JsonNode? value)
        {
            if (Root[section] is not JsonObject obj)
            {
                obj = new JsonObject();
                Root[section] = obj;
            }
            obj[key] = value;
        }

        public string AntiCheat => Get("plugins", "anticheat", "");

        public BackupEntry Save(string detail) => BackupService.WriteFile("Generals Online settings", GamePaths.GoSettings, ToBytes(), detail);
    }

    // The official launcher's launcher.json (client channel and window mode).
    public sealed class LauncherJson
    {
        public JsonObject Root { get; private set; } = new();

        public static LauncherJson Load()
        {
            var settings = new LauncherJson();
            try
            {
                if (File.Exists(GamePaths.LauncherSettings) && JsonNode.Parse(File.ReadAllText(GamePaths.LauncherSettings)) is JsonObject root)
                    settings.Root = root;
            }
            catch { }
            return settings;
        }

        // A value of an unexpected type reads as the default instead of failing
        private T Read<T>(string key, T fallback)
        {
            try
            {
                return Root[key] is { } node ? node.GetValue<T>() : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        // The official launcher stores the channel under a misspelt name (from the profile): true means the live client
        public bool PreferLive
        {
            get => Read(GoProfile.Current.ChannelKey, true);
            set => Root[GoProfile.Current.ChannelKey] = value;
        }

        public bool Windowed
        {
            get => Read("windowed", false);
            set => Root["windowed"] = value;
        }

        public int WindowedWidth
        {
            get => Read("windowed_width", 1920);
            set => Root["windowed_width"] = value;
        }

        public int WindowedHeight
        {
            get => Read("windowed_height", 1080);
            set => Root["windowed_height"] = value;
        }

        // Shared with the official launcher, so an unchanged file is left alone; a change is backed up and can be undone
        public void Save(string detail = "Saved by Command Center")
        {
            if (Load().Root.ToJsonString() == Root.ToJsonString())
                return;
            BackupService.WriteFile("Launcher settings", GamePaths.LauncherSettings,
                System.Text.Encoding.UTF8.GetBytes(Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), detail);
        }
    }
}
