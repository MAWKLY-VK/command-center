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
        private const string StatsUrl = "https://api.playgenerals.online/env/prod/contract/1/Monitoring/BasicStats";

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
            var settings = new GoSettings();
            try
            {
                if (File.Exists(GamePaths.GoSettings) && JsonNode.Parse(File.ReadAllText(GamePaths.GoSettings)) is JsonObject root)
                    settings.Root = root;
            }
            catch
            {
                // A broken file is reported by the health check
            }
            return settings;
        }

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

        public void Set<T>(string section, string key, T value)
        {
            if (Root[section] is not JsonObject obj)
            {
                obj = new JsonObject();
                Root[section] = obj;
            }
            obj[key] = JsonValue.Create(value);
        }

        public string AntiCheat => Get("plugins", "anticheat", "");

        public BackupEntry Save(string detail) =>
            BackupService.WriteFile("Generals Online settings", GamePaths.GoSettings,
                System.Text.Encoding.UTF8.GetBytes(Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), detail);
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

        // The official launcher stores the channel under this (misspelt) name: true means the live client
        public bool PreferLive
        {
            get => Root["prefer_experiemental_client"]?.GetValue<bool>() ?? true;
            set => Root["prefer_experiemental_client"] = value;
        }

        public bool Windowed
        {
            get => Root["windowed"]?.GetValue<bool>() ?? false;
            set => Root["windowed"] = value;
        }

        public int WindowedWidth
        {
            get => Root["windowed_width"]?.GetValue<int>() ?? 1920;
            set => Root["windowed_width"] = value;
        }

        public int WindowedHeight
        {
            get => Root["windowed_height"]?.GetValue<int>() ?? 1080;
            set => Root["windowed_height"] = value;
        }

        // Shared with the official launcher, so an unchanged file is left alone
        public void Save()
        {
            if (Load().Root.ToJsonString() == Root.ToJsonString())
                return;
            Directory.CreateDirectory(GamePaths.GoData);
            File.WriteAllText(GamePaths.LauncherSettings, Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public BackupEntry SaveWithBackup(string detail) =>
            BackupService.WriteFile("Launcher settings", GamePaths.LauncherSettings,
                System.Text.Encoding.UTF8.GetBytes(Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), detail);
    }
}
