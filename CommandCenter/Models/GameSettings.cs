namespace CommandCenter.Models
{
    public class GameSettingsFile_Camera
    {
        public float max_height_only_when_lobby_host { get; set; } = 310f;
        public float min_height { get; set; } = 210f;
        public float move_speed_ratio { get; set; } = 1f;
    }
    public class GameSettingsFile_Chat
    {
        public int duration_seconds_until_fade_out { get; set; } = 30;
    }
    public class GameSettingsFile_Render
    {
        public int fps_limit { get; set; } = 60;
        public bool limit_framerate { get; set; } = true;
        public bool stats_overlay { get; set; } = true;
    }
    public class GameSettingsFile_Social
    {
        public bool notification_friend_comes_online_menus { get; set; } = true;
        public bool notification_friend_comes_online_gameplay { get; set; } = true;
        public bool notification_friend_goes_offline_menus { get; set; } = true;
        public bool notification_friend_goes_offline_gameplay { get; set; } = true;
        public bool notification_player_accepts_request_menus { get; set; } = true;
        public bool notification_player_accepts_request_gameplay { get; set; } = true;
        public bool notification_player_sends_request_menus { get; set; } = true;
        public bool notification_player_sends_request_gameplay { get; set; } = true;
    }
    public class GameSettingsFile_Network
    {
        public int http_version { get; set; } = 0;
        public bool use_alternative_endpoint { get; set; } = false;
    }
    public class GameSettingsFile_DataPacks
    {
        public bool use_community_data_patch { get; set; } = true;
    }
    public class GameSettingsFile_Plugins
    {
        public string anticheat { get; set; } = "";
    }
    public class GameSettingsFile
    {
        public GameSettingsFile_Camera camera { get; set; } = new();
        public GameSettingsFile_Chat chat { get; set; } = new();
        public GameSettingsFile_Render render { get; set; } = new();
        public GameSettingsFile_Social social { get; set; } = new();
        public GameSettingsFile_Network network { get; set; } = new();
        public GameSettingsFile_DataPacks data_packs { get; set; } = new();
        public GameSettingsFile_Plugins plugins { get; set; } = new();
    }

    public enum NatType
    {
        Unknown,
        Open,
        FullCone,
        RestrictedCone,
        PortRestrictedCone,
        Symmetric
    }

    public class NetworkDiagnosticsResult
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // DNS
        public bool DnsSuccess { get; set; }
        public string DnsAddresses { get; set; } = "";

        // Internet baseline (Cloudflare 1.1.1.1)
        public bool CloudflareSuccess { get; set; }
        public int CloudflareAvgMs { get; set; }
        public int CloudflareLossPercent { get; set; }

        // Game server ICMP ping
        public bool PingSuccess { get; set; }
        public int PingAvgMs { get; set; }
        public int PingMinMs { get; set; }
        public int PingMaxMs { get; set; }
        public int PingLossPercent { get; set; }

        // Protocol used for HTTP connection
        public string Protocol { get; set; } = "Unknown";

        // HTTP latency + server status
        public bool HttpSuccess { get; set; }
        public int HttpLatencyMs { get; set; }
        public bool ServerOnline { get; set; }
        public int PlayersOnline { get; set; }
        public int Lobbies { get; set; }

        // Speed test (via Cloudflare speed.cloudflare.com)
        public bool SpeedTestSuccess { get; set; }
        public double DownloadMbps { get; set; }
        public double UploadMbps { get; set; }

        // Geolocation (via Cloudflare meta endpoint, no API key required)
        public bool GeoSuccess { get; set; }
        public string GeoIp { get; set; } = "";
        public string GeoCity { get; set; } = "";       // state/region (US) or empty
        public string GeoCountryCode { get; set; } = ""; // ISO 3166-1 alpha-2
        public string GeoCountry { get; set; } = "";    // full country name
        public string GeoContinent { get; set; } = "";
        public string GeoIsp { get; set; } = "";

        // CDN reachability
        public bool CdnSuccess { get; set; }
        public int CdnLatencyMs { get; set; }

        // STUN reachability
        public bool StunSuccess { get; set; }
        public int StunLatencyMs { get; set; }
        public string StunExternalEndpoint { get; set; } = "";

        // TURN reachability
        public bool TurnSuccess { get; set; }
        public int TurnLatencyMs { get; set; }
        public string TurnEndpoint { get; set; } = "";

        // NAT type
        public NatType NatType { get; set; } = NatType.Unknown;
        public string NatTypeDetail { get; set; } = "";
    }
}
