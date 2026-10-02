using CommandCenter.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CommandCenter.Services
{
    internal sealed class NetworkDiagnosticsService
    {
        private const string GameApiHost = "api.playgenerals.online";
        private const string GameApiUrl  = "https://api.playgenerals.online/env/prod/contract/1/Monitoring/BasicStats";
        private const string SpeedUpUrl   = "https://speed.cloudflare.com/__up";
        private const string StunHost    = "stun.playgenerals.online";
        private const int    StunPort    = 3478;
        private const string StunAltHost = "stun.cloudflare.com"; // used for symmetric NAT detection
        private const int    StunAltPort = 3478;
        private const string TurnHost    = "turn.playgenerals.online";
        private static readonly int[] TurnPorts = [3478, 5349];
        private const string CloudflareIp = "1.1.1.1";
        private const int    PingCount    = 4;

        public async Task<NetworkDiagnosticsResult> RunAsync(CancellationToken ct = default)
        {
            var result = new NetworkDiagnosticsResult();

            // Phase 1: DNS + Cloudflare baseline + geolocation (all independent)
            var dnsTask = CheckDnsAsync(GameApiHost, ct);
            var cfTask  = CheckIcmpPingAsync(CloudflareIp, PingCount, ct);
            var geoTask = CheckGeoAsync(ct);
            await Task.WhenAll(dnsTask, cfTask, geoTask);

            var dns = dnsTask.Result;
            result.DnsSuccess            = dns.Success;
            result.DnsAddresses          = dns.Addresses;

            var cf = cfTask.Result;
            result.CloudflareSuccess     = cf.Success;
            result.CloudflareAvgMs       = cf.AvgMs;
            result.CloudflareLossPercent = cf.LossPercent;

            var geo = geoTask.Result;
            result.GeoSuccess     = geo.Success;
            result.GeoIp          = geo.Ip;
            result.GeoCity        = geo.State;
            result.GeoCountryCode = geo.CountryCode;
            result.GeoCountry     = geo.Country;
            result.GeoContinent   = geo.Continent;
            result.GeoIsp         = geo.Isp;

            // Phase 2: All remaining checks (run in parallel)
            var pingTask    = CheckIcmpPingAsync(GameApiHost, PingCount, ct);
            var httpTask    = CheckHttpAsync(GameApiUrl, ct);
            var cdnTask     = CheckCdnAsync(ct);
            var stunNatTask = CheckStunAndNatAsync(ct);
            var turnTask    = CheckTurnAsync(ct);
            var speedTask   = CheckSpeedAsync(ct);
            await Task.WhenAll(pingTask, httpTask, cdnTask, stunNatTask, turnTask, speedTask);

            var ping = pingTask.Result;
            result.PingSuccess     = ping.Success;
            result.PingAvgMs       = ping.AvgMs;
            result.PingMinMs       = ping.MinMs;
            result.PingMaxMs       = ping.MaxMs;
            result.PingLossPercent = ping.LossPercent;

            var http = httpTask.Result;
            result.HttpSuccess   = http.Success;
            result.HttpLatencyMs = http.LatencyMs;
            result.Protocol      = http.Protocol;
            result.ServerOnline  = http.ServerOnline;
            result.PlayersOnline = http.Players;
            result.Lobbies       = http.Lobbies;

            var cdn = cdnTask.Result;
            result.CdnSuccess   = cdn.Success;
            result.CdnLatencyMs = cdn.LatencyMs;

            var sn = stunNatTask.Result;
            result.StunSuccess          = sn.StunSuccess;
            result.StunLatencyMs        = sn.StunLatencyMs;
            result.StunExternalEndpoint = sn.ExternalEndpoint;
            result.NatType              = sn.NatType;
            result.NatTypeDetail        = sn.NatTypeDetail;

            var turn = turnTask.Result;
            result.TurnSuccess   = turn.Success;
            result.TurnLatencyMs = turn.LatencyMs;
            result.TurnEndpoint  = turn.Endpoint;

            var speed = speedTask.Result;
            result.SpeedTestSuccess = speed.Success;
            result.DownloadMbps     = speed.DownloadMbps;
            result.UploadMbps       = speed.UploadMbps;

            return result;
        }

        // ─── DNS ─────────────────────────────────────────────────────────────────

        private record DnsResult(bool Success, string Addresses);

        private static async Task<DnsResult> CheckDnsAsync(string host, CancellationToken ct)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, ct);
                if (addrs.Length == 0) return new(false, "No addresses returned");
                return new(true, string.Join(", ", addrs.Select(a => a.ToString())));
            }
            catch (Exception ex) { return new(false, ex.Message); }
        }

        // ─── ICMP Ping ────────────────────────────────────────────────────────────

        private record PingResult(bool Success, int AvgMs, int MinMs, int MaxMs, int LossPercent);

        private static async Task<PingResult> CheckIcmpPingAsync(string host, int count, CancellationToken ct)
        {
            var times = new List<long>();
            int lost = 0;
            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var ping = new Ping();
                    var task = ping.SendPingAsync(host, 2000);
                    if (await Task.WhenAny(task, Task.Delay(2500, ct)) == task)
                    {
                        var reply = await task;
                        if (reply.Status == IPStatus.Success)
                            times.Add(reply.RoundtripTime);
                        else
                            lost++;
                    }
                    else
                    {
                        lost++;
                    }
                }
                catch { lost++; }

                if (i < count - 1)
                    await Task.Delay(250, ct).ConfigureAwait(false);
            }
            if (times.Count == 0) return new(false, 0, 0, 0, 100);
            return new(true,
                (int)times.Average(),
                (int)times.Min(),
                (int)times.Max(),
                (int)Math.Round(lost * 100.0 / count));
        }

        // ─── HTTP Latency + Server Status ─────────────────────────────────────────

        private record HttpResult(bool Success, int LatencyMs, string Protocol, bool ServerOnline, int Players, int Lobbies);
        private record StatsJson(
            [property: JsonPropertyName("players")] int Players,
            [property: JsonPropertyName("lobbies")] int Lobbies);

        private static async Task<HttpResult> CheckHttpAsync(string url, CancellationToken ct)
        {
            string protocol = "Unknown";
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ConnectCallback = async (ctx, innerCt) =>
                {
                    var sock = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await sock.ConnectAsync(ctx.DnsEndPoint, innerCt);
                        protocol = sock.RemoteEndPoint is IPEndPoint ep
                            ? (ep.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")
                            : "Unknown";
                        return new NetworkStream(sock, ownsSocket: true);
                    }
                    catch { sock.Dispose(); throw; }
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var sw = Stopwatch.StartNew();
            try
            {
                var resp = await client.GetAsync(url, ct);
                sw.Stop();
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    var stats = System.Text.Json.JsonSerializer.Deserialize<StatsJson>(body);
                    return new(true, (int)sw.Elapsed.TotalMilliseconds, protocol, true,
                        stats?.Players ?? 0, stats?.Lobbies ?? 0);
                }
                return new(true, (int)sw.Elapsed.TotalMilliseconds, protocol, false, 0, 0);
            }
            catch
            {
                sw.Stop();
                return new(false, 0, "Unknown", false, 0, 0);
            }
        }

        // ─── CDN reachability ─────────────────────────────────────────────────────

        private record CdnResult(bool Success, int LatencyMs);

        private static async Task<CdnResult> CheckCdnAsync(CancellationToken ct)
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
            using var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var sw = Stopwatch.StartNew();
            try
            {
                var resp = await client.GetAsync("https://cdn.playgenerals.online/manifest.json",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                return new(resp.IsSuccessStatusCode, (int)sw.Elapsed.TotalMilliseconds);
            }
            catch { sw.Stop(); return new(false, 0); }
        }

        // ─── Geolocation (ip-api.com — no API key required) ──────────────────────

        private record GeoResult(bool Success, string Ip, string State, string CountryCode, string Country, string Continent, string Isp);

        private record IpApiResponse(
            [property: JsonPropertyName("status")]      string?  Status,
            [property: JsonPropertyName("query")]       string?  Query,
            [property: JsonPropertyName("city")]        string?  City,
            [property: JsonPropertyName("country")]     string?  Country,
            [property: JsonPropertyName("countryCode")] string?  CountryCode,
            [property: JsonPropertyName("regionName")]  string?  RegionName,
            [property: JsonPropertyName("isp")]         string?  Isp,
            [property: JsonPropertyName("continent")]   string?  Continent);

        private static async Task<GeoResult> CheckGeoAsync(CancellationToken ct)
        {
            try
            {
                using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
                using var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                // ip-api.com is free, no key required; HTTP required on free tier
                var json = await client.GetStringAsync(
                    "http://ip-api.com/json?fields=status,country,countryCode,city,regionName,isp,continent,query", ct);
                var meta = System.Text.Json.JsonSerializer.Deserialize<IpApiResponse>(json);
                if (meta?.Status != "success") return new(false, "", "", "", "", "", "");

                string countryCode = meta.CountryCode ?? "";
                string fullName    = countryCode == "US" ? "United States of America" : (meta.Country ?? "");

                return new(true,
                    meta.Query      ?? "",
                    meta.RegionName ?? "",   // state for US, empty otherwise
                    countryCode,
                    fullName,
                    meta.Continent  ?? "",
                    meta.Isp        ?? "");
            }
            catch { return new(false, "", "", "", "", "", ""); }
        }

        // ─── Speed Test (Cloudflare) ──────────────────────────────────────────────
        // Uses multiple parallel TCP streams (like Speedtest.net) so a single
        // connection's TCP slow-start doesn't under-report fast connections.
        // Each phase is time-bounded (6 s) so slow connections also get an accurate
        // measurement without waiting forever.

        private const int SpeedStreams   = 4;
        private const int SpeedPhaseSecs = 6;

        private record SpeedResult(bool Success, double DownloadMbps, double UploadMbps);

        private static Task<SpeedResult> CheckSpeedAsync(CancellationToken ct) =>
            // Run entirely on the thread pool so the tight ReadAsync loop never
            // competes with the WPF UI SynchronizationContext.
            Task.Run(() => RunSpeedTestAsync(ct), ct);

        private static async Task<SpeedResult> RunSpeedTestAsync(CancellationToken ct)
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };

            double dlMbps = 0, ulMbps = 0;

            // ── Download ─────────────────────────────────────────────────────────
            try
            {
                using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dlCts.CancelAfter(TimeSpan.FromSeconds(SpeedPhaseSecs));

                var sw = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, SpeedStreams)
                    .Select(_ => DownloadStreamAsync(client, dlCts.Token))
                    .ToArray();

                long totalBytes = 0;
                foreach (var t in tasks)
                    try { totalBytes += await t.ConfigureAwait(false); } catch { }
                sw.Stop();

                if (totalBytes > 0 && sw.Elapsed.TotalSeconds > 0)
                    dlMbps = totalBytes * 8.0 / (sw.Elapsed.TotalSeconds * 1_000_000.0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }

            // ── Upload ───────────────────────────────────────────────────────────
            try
            {
                using var ulCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ulCts.CancelAfter(TimeSpan.FromSeconds(SpeedPhaseSecs));

                var sw = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, SpeedStreams)
                    .Select(_ => UploadStreamAsync(client, ulCts.Token))
                    .ToArray();

                long totalBytes = 0;
                foreach (var t in tasks)
                    try { totalBytes += await t.ConfigureAwait(false); } catch { }
                sw.Stop();

                if (totalBytes > 0 && sw.Elapsed.TotalSeconds > 0)
                    ulMbps = totalBytes * 8.0 / (sw.Elapsed.TotalSeconds * 1_000_000.0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }

            return new(dlMbps > 0 || ulMbps > 0, dlMbps, ulMbps);
        }

        private static async Task<long> DownloadStreamAsync(HttpClient client, CancellationToken ct)
        {
            long total = 0;
            try
            {
                // Loop requests so fast connections aren't starved when a single
                // 50 MB response is exhausted before the time window expires.
                while (!ct.IsCancellationRequested)
                {
                    using var resp = await client.GetAsync(
                        "https://speed.cloudflare.com/__down?bytes=50000000",
                        HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) break;
                    using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var buf = new byte[131072];
                    int read;
                    while ((read = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                        total += read;
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            return total;
        }

        // Repeatedly POST 1 MB chunks until the time window expires.
        private static async Task<long> UploadStreamAsync(HttpClient client, CancellationToken ct)
        {
            var chunk = new byte[1_000_000]; // 1 MB of zeros
            long total = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var content = new ByteArrayContent(chunk);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    var resp = await client.PostAsync(SpeedUpUrl, content, ct);
                    if (resp.IsSuccessStatusCode)
                        total += chunk.Length;
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
            return total;
        }

        // ─── STUN + NAT Type Detection ────────────────────────────────────────────
        //
        // Algorithm (RFC 3489-style):
        //   Test I   → Basic binding request. If external IP == local IP → Open Internet.
        //   Test I'  → Same socket (same local port), different destination (stun.cloudflare.com).
        //              If external port changes → Symmetric NAT.
        //   Test II  → CHANGE-REQUEST change-IP+port. Response from different source → Full Cone.
        //   Test III → CHANGE-REQUEST change-port only. Response from different source → Restricted Cone.
        //              No response → Port Restricted Cone.
        //              Response from same source (RFC 5389 server) → Cone type undetermined.

        private record StunNatResult(bool StunSuccess, int StunLatencyMs, string ExternalEndpoint,
            NatType NatType, string NatTypeDetail);

        private static async Task<StunNatResult> CheckStunAndNatAsync(CancellationToken ct)
        {
            try
            {
                var stunAddrs = await Dns.GetHostAddressesAsync(StunHost, AddressFamily.Unspecified, ct);
                var stunIp = stunAddrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                             ?? stunAddrs.FirstOrDefault();
                if (stunIp == null)
                    return new(false, 0, "", NatType.Unknown, "DNS resolution failed for STUN host");

                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // Suppress Windows ICMP port-unreachable errors on UDP sockets (SIO_UDP_CONNRESET)
                try { socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch { }
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                // Test I: Get external IP:port mapping and measure latency
                var (extEP, msI, _) = await StunTransactAsync(socket, stunIp, StunPort, null, ct);
                if (extEP == null)
                    return new(false, 0, "", NatType.Unknown, "No response from STUN server");

                // Open Internet: external IP equals local IP (no NAT)
                var localIp = GetLocalIpAddress();
                if (localIp != null && extEP.Address.Equals(localIp))
                    return new(true, msI, extEP.ToString(), NatType.Open,
                        "No NAT — directly connected to internet");

                // Test I': Symmetric detection — same local port, different remote destination.
                // Cone NATs keep the same external port regardless of destination.
                // Symmetric NATs assign a different external port per destination.
                try
                {
                    DrainSocket(socket);
                    var altAddrs = await Dns.GetHostAddressesAsync(StunAltHost, AddressFamily.Unspecified, ct);
                    var altIp = altAddrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                                ?? altAddrs.FirstOrDefault();
                    if (altIp != null)
                    {
                        var (extEPAlt, _, _) = await StunTransactAsync(socket, altIp, StunAltPort, null, ct);
                        if (extEPAlt != null && extEPAlt.Port != extEP.Port)
                            return new(true, msI, extEP.ToString(), NatType.Symmetric,
                                "Symmetric NAT — direct P2P may fail; TURN relay will be used");
                    }
                }
                catch { /* non-critical; proceed to cone sub-type classification */ }

                // Tests II & III: CHANGE-REQUEST (RFC 3489) cone sub-type classification.
                // If the server honours CHANGE-REQUEST it responds from a different source IP:port.
                // RFC 5389-only servers ignore the attribute and respond from the same source.
                var sentToEP = new IPEndPoint(stunIp, StunPort);

                // Test II: CHANGE-REQUEST with change-IP + change-port → Full Cone
                DrainSocket(socket);
                var (extII, _, srcII) = await StunTransactAsync(socket, stunIp, StunPort, 0x06, ct);
                if (extII != null && srcII != null && !srcII.Equals(sentToEP))
                    return new(true, msI, extEP.ToString(), NatType.FullCone,
                        "Full Cone NAT — best for P2P gaming");

                // Test III: CHANGE-REQUEST with change-port only → Restricted Cone
                DrainSocket(socket);
                var (extIII, _, srcIII) = await StunTransactAsync(socket, stunIp, StunPort, 0x02, ct);
                if (extIII != null && srcIII != null && !srcIII.Equals(sentToEP))
                    return new(true, msI, extEP.ToString(), NatType.RestrictedCone,
                        "P2P works after you initiate the connection");

                // No response to Test III → port-filtered by NAT
                if (extIII == null)
                    return new(true, msI, extEP.ToString(), NatType.PortRestrictedCone,
                        "P2P works with STUN hole-punching");

                // Both tests received a response from same IP:port → RFC 5389 server, cannot sub-classify
                return new(true, msI, extEP.ToString(), NatType.PortRestrictedCone,
                    "Cone NAT (type undetermined) — P2P should work");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new(false, 0, "", NatType.Unknown, $"Error: {ex.Message}");
            }
        }

        private static async Task<(IPEndPoint? MappedEP, int Ms, IPEndPoint? SourceEP)> StunTransactAsync(
            Socket socket, IPAddress serverIp, int port, byte? changeFlags, CancellationToken ct)
        {
            var packet = BuildStunRequest(changeFlags);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);
            var sw = Stopwatch.StartNew();
            try
            {
                await socket.SendToAsync(packet.AsMemory(), SocketFlags.None,
                    new IPEndPoint(serverIp, port), cts.Token);
                var buf = new byte[512];
                var recv = await socket.ReceiveFromAsync(buf.AsMemory(), SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), cts.Token);
                sw.Stop();
                var srcEP  = recv.RemoteEndPoint as IPEndPoint;
                var mapped = ParseMappedAddress(buf.AsSpan(0, recv.ReceivedBytes));
                return (mapped, (int)sw.Elapsed.TotalMilliseconds, srcEP);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                sw.Stop();
                return (null, 0, null);
            }
        }

        private static byte[] BuildStunRequest(byte? changeFlags)
        {
            var txId = new byte[12];
            Random.Shared.NextBytes(txId);
            if (changeFlags == null)
            {
                // Standard 20-byte Binding Request (RFC 5389)
                var pkt = new byte[20];
                pkt[0] = 0x00; pkt[1] = 0x01;              // Message Type: Binding Request
                                                             // pkt[2-3] = 0x0000 (length = 0)
                pkt[4] = 0x21; pkt[5] = 0x12; pkt[6] = 0xA4; pkt[7] = 0x42; // Magic Cookie
                Array.Copy(txId, 0, pkt, 8, 12);
                return pkt;
            }
            else
            {
                // 28-byte Binding Request with CHANGE-REQUEST attribute (RFC 3489)
                var pkt = new byte[28];
                pkt[0] = 0x00; pkt[1] = 0x01;
                pkt[2] = 0x00; pkt[3] = 0x08;              // Message Length: 8
                pkt[4] = 0x21; pkt[5] = 0x12; pkt[6] = 0xA4; pkt[7] = 0x42;
                Array.Copy(txId, 0, pkt, 8, 12);
                pkt[20] = 0x00; pkt[21] = 0x03;            // Attr Type: CHANGE-REQUEST (0x0003)
                pkt[22] = 0x00; pkt[23] = 0x04;            // Attr Length: 4
                                                            // pkt[24-26] = 0x000000
                pkt[27] = changeFlags.Value;                // 0x06 = change IP+port, 0x02 = change port only
                return pkt;
            }
        }

        private static IPEndPoint? ParseMappedAddress(ReadOnlySpan<byte> data)
        {
            if (data.Length < 20) return null;
            if ((data[0] << 8 | data[1]) != 0x0101) return null; // Must be Binding Success Response
            int msgLen = data[2] << 8 | data[3];
            int i = 20;
            while (i + 4 <= data.Length && i < 20 + msgLen)
            {
                int type = data[i] << 8 | data[i + 1];
                int len  = data[i + 2] << 8 | data[i + 3];
                i += 4;
                if (i + len > data.Length) break;

                if (type == 0x0020 && len >= 8 && data[i + 1] == 0x01) // XOR-MAPPED-ADDRESS, IPv4
                {
                    int port = ((data[i + 2] << 8) | data[i + 3]) ^ 0x2112;
                    byte[] addr =
                    [
                        (byte)(data[i + 4] ^ 0x21),
                        (byte)(data[i + 5] ^ 0x12),
                        (byte)(data[i + 6] ^ 0xA4),
                        (byte)(data[i + 7] ^ 0x42)
                    ];
                    return new IPEndPoint(new IPAddress(addr), port);
                }
                if (type == 0x0001 && len >= 8 && data[i + 1] == 0x01) // MAPPED-ADDRESS, IPv4
                {
                    int port = (data[i + 2] << 8) | data[i + 3];
                    return new IPEndPoint(new IPAddress(data.Slice(i + 4, 4).ToArray()), port);
                }

                i += len + (len % 4 != 0 ? 4 - len % 4 : 0); // advance past attribute + padding
            }
            return null;
        }

        // ─── TURN TCP Reachability ────────────────────────────────────────────────

        private record TurnResult(bool Success, int LatencyMs, string Endpoint);

        private static async Task<TurnResult> CheckTurnAsync(CancellationToken ct)
        {
            foreach (int port in TurnPorts)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                        { NoDelay = true };
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(3000);
                    await sock.ConnectAsync(TurnHost, port, cts.Token);
                    sw.Stop();
                    return new(true, (int)sw.Elapsed.TotalMilliseconds, $"{TurnHost}:{port}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
                sw.Stop();
            }
            return new(false, 0, "");
        }

        // ─── Helpers ─────────────────────────────────────────────────────────────

        // Drain any stale buffered packets before each STUN test to avoid cross-test contamination.
        private static void DrainSocket(Socket socket)
        {
            var buf = new byte[512];
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            while (socket.Poll(0, SelectMode.SelectRead))
                try { socket.ReceiveFrom(buf, ref ep); } catch { break; }
        }

        private static IPAddress? GetLocalIpAddress()
        {
            try
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.Connect("8.8.8.8", 80);
                return (sock.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch { return null; }
        }
    }
}
