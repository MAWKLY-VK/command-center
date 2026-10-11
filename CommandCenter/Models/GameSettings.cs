namespace CommandCenter.Models
{
    // Generals Online's settings.json is read and written as a JSON tree (see GoSettings), so a field with an unexpected
    // type or one added by a newer version never stops the Options page from loading.

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
