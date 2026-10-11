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

        // Country only, from Cloudflare's trace page over HTTPS. The address and the internet provider are not kept.
        public bool GeoSuccess { get; set; }
        public string GeoCountryCode { get; set; } = ""; // ISO 3166-1 alpha-2

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
