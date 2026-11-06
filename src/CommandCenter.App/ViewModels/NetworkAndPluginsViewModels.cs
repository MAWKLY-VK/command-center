using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommandCenter.ViewModels
{
    // Info is a fact that is not a measurement, such as the country, and gets no rating
    public enum DiagnosticLevel { Checking, Good, Fair, Bad, Unknown, Info }

    public sealed record DiagnosticRow(string Group, string Name, DiagnosticLevel Level, string Text)
    {
        // The level in words beside its icon, so a state is never shown by colour alone
        public string LevelText => Level switch
        {
            DiagnosticLevel.Checking => "Checking",
            DiagnosticLevel.Good => "Good",
            DiagnosticLevel.Fair => "Fair",
            DiagnosticLevel.Bad => "Problem",
            DiagnosticLevel.Info => "",
            _ => "Unknown",
        };
    }

    public sealed record DiagnosticGroup(string Name, IReadOnlyList<DiagnosticRow> Rows);

    // One earlier run in the log, the values worth comparing between runs
    public sealed record DiagnosticRun(string Time, string Internet, string Ping, string Loss, string Http, string Nat, string Server);

    // Settings › Network's diagnostics (ticket 09): ping, loss, NAT and the servers, measured on the direct route the game
    // uses, with a log of the last ten runs and a copy for a help request that hides addresses and names. Moved from the
    // Options page's code-behind; the rows and the copy keep their texts.
    public sealed partial class NetworkDiagnosticsViewModel : ObservableObject
    {
        private const int HistoryLength = 10;

        private readonly Func<CancellationToken, Task<NetworkDiagnosticsResult>> _run;
        private readonly Action<string> _copy;
        private NetworkDiagnosticsResult? _last;

        public NetworkDiagnosticsViewModel(Func<CancellationToken, Task<NetworkDiagnosticsResult>> run, Action<string> copy)
        {
            _run = run;
            _copy = copy;
            History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Groups), nameof(HasRows), nameof(NotRunYet), nameof(HasProblems))]
        private IReadOnlyList<DiagnosticRow> _rows = [];

        [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CopyCommand))] private bool _hasResult;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(RunLabel))] private bool _isRunning;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFailure), nameof(NotRunYet))] private string _failure = "";

        // The rows by group, in the order they are measured
        public IReadOnlyList<DiagnosticGroup> Groups => Rows.GroupBy(r => r.Group).Select(g => new DiagnosticGroup(g.Key, g.ToList())).ToList();
        public bool HasRows => Rows.Count > 0;

        // A row that needs attention, which brings the next step under the results
        public bool HasProblems => Rows.Any(r => r.Level == DiagnosticLevel.Bad);
        public bool NotRunYet => !HasRows && !HasFailure;
        public bool HasFailure => Failure.Length > 0;
        public ObservableCollection<DiagnosticRun> History { get; } = [];
        public bool HasHistory => History.Count > 0;
        public string RunLabel => IsRunning ? "Running…" : "Run diagnostics";

        // The button stays off while a run is going (the command allows one at a time)
        [RelayCommand]
        private async Task RunAsync()
        {
            var before = Rows;
            IsRunning = true;
            Failure = "";
            Rows = RowsFor(null);
            try
            {
                var result = await _run(CancellationToken.None);
                _last = result;
                Rows = RowsFor(result);
                History.Insert(0, Run(result));
                while (History.Count > HistoryLength)
                    History.RemoveAt(History.Count - 1);
                HasResult = true;
            }
            catch (Exception ex)
            {
                Rows = before;
                Failure = $"The check could not run ({ex.Message.TrimEnd('.')}). Check the internet connection and try again.";
            }
            finally
            {
                IsRunning = false;
            }
        }

        [RelayCommand(CanExecute = nameof(HasResult))]
        private void Copy()
        {
            if (_last != null)
                _copy(Report(_last));
        }

        // The rows on screen; without a result yet, every row is still being checked
        public static IReadOnlyList<DiagnosticRow> RowsFor(NetworkDiagnosticsResult? r)
        {
            if (r == null)
                return Names.Select(n => new DiagnosticRow(n.Group, n.Name, DiagnosticLevel.Checking, "checking…")).ToList();
            return
            [
                new("Internet", "Your country", r.GeoSuccess ? DiagnosticLevel.Info : DiagnosticLevel.Unknown,
                    r.GeoSuccess ? CountryName(r.GeoCountryCode) : "Could not determine the country"),
                new("Internet", "Cloudflare", r.CloudflareSuccess ? Loss(r.CloudflareLossPercent) : DiagnosticLevel.Bad,
                    r.CloudflareSuccess ? $"{r.CloudflareAvgMs} ms avg, {r.CloudflareLossPercent}% loss" : "Unreachable"),
                new("Game server", "DNS resolution", r.DnsSuccess ? DiagnosticLevel.Good : DiagnosticLevel.Bad,
                    r.DnsSuccess ? r.DnsAddresses : "Resolution failed"),
                new("Game server", "Server ping (ICMP)", r.PingSuccess ? Latency(r.PingAvgMs) : DiagnosticLevel.Bad,
                    r.PingSuccess ? $"{r.PingAvgMs} ms avg (min {r.PingMinMs} / max {r.PingMaxMs}), {r.PingLossPercent}% loss" : "Unreachable (ICMP may be blocked)"),
                new("Game server", "Protocol", r.Protocol is "IPv4" or "IPv6" ? DiagnosticLevel.Info : DiagnosticLevel.Unknown, r.Protocol),
                new("Game server", "HTTP latency", r.HttpSuccess ? Latency(r.HttpLatencyMs) : DiagnosticLevel.Bad,
                    r.HttpSuccess ? $"{r.HttpLatencyMs} ms" : "Request failed"),
                new("Game server", "Server status", r.ServerOnline ? DiagnosticLevel.Good : DiagnosticLevel.Bad,
                    r.ServerOnline ? $"Online, {r.PlayersOnline} players and {r.Lobbies} lobbies" : r.HttpSuccess ? "Degraded (HTTP error)" : "Offline or unreachable"),
                new("Connectivity", "CDN (cdn.playgenerals.online)", r.CdnSuccess ? Latency(r.CdnLatencyMs) : DiagnosticLevel.Bad,
                    r.CdnSuccess ? $"{r.CdnLatencyMs} ms" : "Unreachable"),
                new("Connectivity", "STUN (stun.playgenerals.online)", r.StunSuccess ? Latency(r.StunLatencyMs) : DiagnosticLevel.Bad,
                    r.StunSuccess ? $"{r.StunLatencyMs} ms ({Redaction.Report(r.StunExternalEndpoint)})" : "Unreachable"),
                new("Connectivity", "TURN (turn.playgenerals.online)", r.TurnSuccess ? Latency(r.TurnLatencyMs) : DiagnosticLevel.Bad,
                    r.TurnSuccess ? $"{r.TurnLatencyMs} ms ({r.TurnEndpoint})" : "Unreachable"),
                new("Connectivity", "NAT type", Nat(r.NatType), NatText(r)),
            ];
        }

        private static readonly (string Group, string Name)[] Names =
        [
            ("Internet", "Your country"), ("Internet", "Cloudflare"),
            ("Game server", "DNS resolution"), ("Game server", "Server ping (ICMP)"), ("Game server", "Protocol"),
            ("Game server", "HTTP latency"), ("Game server", "Server status"),
            ("Connectivity", "CDN (cdn.playgenerals.online)"), ("Connectivity", "STUN (stun.playgenerals.online)"),
            ("Connectivity", "TURN (turn.playgenerals.online)"), ("Connectivity", "NAT type"),
        ];

        // The copy for a help request: addresses (the player's own among them) and the Windows user and PC names are
        // hidden, as in the support report
        public static string Report(NetworkDiagnosticsResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ Generals Online Network Diagnostics ═══");
            sb.AppendLine($"Time:         {r.Timestamp:yyyy-MM-dd HH:mm:ss}");
            if (r.GeoSuccess)
                sb.AppendLine($"Country:      {CountryName(r.GeoCountryCode)}");
            sb.AppendLine();
            sb.AppendLine("─ Internet ─");
            sb.AppendLine($"Cloudflare:   {(r.CloudflareSuccess ? $"{r.CloudflareAvgMs}ms avg, {r.CloudflareLossPercent}% loss" : "Unreachable")}");
            sb.AppendLine();
            sb.AppendLine("─ Game Server ─");
            sb.AppendLine($"DNS:          {(r.DnsSuccess ? $"OK — {r.DnsAddresses}" : "FAILED")}");
            sb.AppendLine($"Ping (ICMP):  {(r.PingSuccess ? $"{r.PingAvgMs}ms avg (min {r.PingMinMs} / max {r.PingMaxMs}), {r.PingLossPercent}% loss" : "Unreachable")}");
            sb.AppendLine($"Protocol:     {r.Protocol}");
            sb.AppendLine($"HTTP Latency: {(r.HttpSuccess ? $"{r.HttpLatencyMs}ms" : "Failed")}");
            sb.AppendLine($"Server:       {(r.ServerOnline ? $"Online — {r.PlayersOnline} players, {r.Lobbies} lobbies" : "Offline / Unreachable")}");
            sb.AppendLine();
            sb.AppendLine("─ Connectivity ─");
            sb.AppendLine($"CDN:          {(r.CdnSuccess ? $"{r.CdnLatencyMs}ms" : "Unreachable")}");
            sb.AppendLine($"STUN:         {(r.StunSuccess ? $"OK — {r.StunExternalEndpoint} ({r.StunLatencyMs}ms)" : "Unreachable")}");
            sb.AppendLine($"TURN:         {(r.TurnSuccess ? $"OK — {r.TurnEndpoint} ({r.TurnLatencyMs}ms)" : "Unreachable")}");
            sb.AppendLine($"NAT Type:     {r.NatType} — {r.NatTypeDetail}");
            return Redaction.Report(sb.ToString());
        }

        private static DiagnosticRun Run(NetworkDiagnosticsResult r) => new(
            r.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            r.CloudflareSuccess ? $"{r.CloudflareAvgMs} ms" : "Failed",
            r.PingSuccess ? $"{r.PingAvgMs} ms" : "Failed",
            r.PingSuccess ? $"{r.PingLossPercent}%" : "Failed",
            r.HttpSuccess ? $"{r.HttpLatencyMs} ms" : "Failed",
            NatLabel(r.NatType),
            r.ServerOnline ? "Online" : "Offline");

        private static DiagnosticLevel Latency(int ms) => ms <= 60 ? DiagnosticLevel.Good : ms <= 150 ? DiagnosticLevel.Fair : DiagnosticLevel.Bad;
        private static DiagnosticLevel Loss(int percent) => percent == 0 ? DiagnosticLevel.Good : percent < 25 ? DiagnosticLevel.Fair : DiagnosticLevel.Bad;

        private static DiagnosticLevel Nat(NatType type) => type switch
        {
            NatType.Open or NatType.FullCone or NatType.RestrictedCone => DiagnosticLevel.Good,
            NatType.PortRestrictedCone => DiagnosticLevel.Fair,
            NatType.Symmetric => DiagnosticLevel.Bad,
            _ => DiagnosticLevel.Unknown,
        };

        private static string NatText(NetworkDiagnosticsResult r) => r.NatType switch
        {
            NatType.Open or NatType.FullCone or NatType.RestrictedCone => $"{NatLabel(r.NatType)} — {r.NatTypeDetail}",
            NatType.PortRestrictedCone => $"Port Restricted Cone — {r.NatTypeDetail}",
            NatType.Symmetric => $"Symmetric — {r.NatTypeDetail}",
            _ => $"Unknown — {r.NatTypeDetail}",
        };

        private static string NatLabel(NatType type) => type switch
        {
            NatType.Open => "Open",
            NatType.FullCone => "Full Cone",
            NatType.RestrictedCone => "Restricted",
            NatType.PortRestrictedCone => "Port Restricted",
            NatType.Symmetric => "Symmetric",
            _ => "Unknown",
        };

        // The country's English name
        private static string CountryName(string code)
        {
            try
            {
                return new RegionInfo(code).EnglishName;
            }
            catch (ArgumentException)
            {
                return code;
            }
        }
    }

    // A line of a plugin's files: a file's name as a heading, a value from it, or a note in words (a file that could not
    // be read), which is not file data
    public sealed record PluginLine(string Text, int Indent, bool IsHeading, bool IsNote = false)
    {
        // Shown in the data face, where two spaces a level line nested values up under their key
        public string Shown => new string(' ', Indent * 2) + Text;
    }

    // Settings › Plugins (ticket 09): the anti-cheat plugins in the game folder's plugins folder, the one settings.json
    // names chosen, and the contents of the chosen one's files. Picking another writes it with the other settings.
    public sealed partial class PluginsViewModel : ObservableObject
    {
        // A plugin's settings file is a few hundred bytes; anything far larger is not one
        private const long FileLimit = 1024 * 1024;

        private readonly GameSettingsViewModel _settings;
        private readonly Func<string> _gameFolder;
        private string? _selected;
        private bool _filling;

        public PluginsViewModel(GameSettingsViewModel settings, Func<string> gameFolder)
        {
            _settings = settings;
            _gameFolder = gameFolder;
            Refresh();
        }

        // Each plugin folder, by the plugin_name in its first file; choosing one picks it
        public IReadOnlyList<Choice<string>> Plugins { get; private set; } = [];
        public IReadOnlyList<PluginLine> Details { get; private set; } = [];
        public bool IsEmpty => Plugins.Count == 0;
        public bool HasPlugins => !IsEmpty;

        // Plugins are there but settings.json names none of them: Generals Online picks one itself
        public bool NoneChosen => HasPlugins && Selected == null;
        public bool ShowDetails => HasPlugins && Selected != null;
        public string Folder => Path.Combine(_gameFolder(), "plugins");

        public string EmptyText => _gameFolder().Length == 0
            ? "The game was not found. Choose its folder in Game folder first."
            : $"No anti-cheat plugins were found in {Folder}. Install or update Generals Online, then open Plugins again.";

        // The plugin folder chosen; the player's pick goes to settings.json when Settings is left
        public string? Selected
        {
            get => _selected;
            set
            {
                if (value == _selected)
                    return;
                _selected = value;
                if (!_filling && value != null)
                    _settings.ChooseAntiCheat(value);
                Details = value == null ? [] : Read(value);
                foreach (var plugin in Plugins)
                    plugin.Refresh();
                OnPropertyChanged();
                OnPropertyChanged(nameof(Details));
                OnPropertyChanged(nameof(NoneChosen));
                OnPropertyChanged(nameof(ShowDetails));
            }
        }

        // Read again each time Plugins opens, and after the game folder changes
        public void Refresh()
        {
            _filling = true;
            Plugins = List().Select(p => new Choice<string>(p.Folder, p.Name, () => Selected ?? "", chosen => Selected = chosen)).ToList();
            _selected = "";
            Selected = Plugins.Any(p => p.Value == _settings.AntiCheat) ? _settings.AntiCheat : null;
            _filling = false;
            OnPropertyChanged(nameof(Plugins));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasPlugins));
            OnPropertyChanged(nameof(Folder));
            OnPropertyChanged(nameof(EmptyText));
        }

        // Each folder with a settings file, named by the plugin_name in its first file
        private List<(string Folder, string Name)> List()
        {
            try
            {
                if (_gameFolder().Length == 0 || !Directory.Exists(Folder))
                    return [];
                return Directory.GetDirectories(Folder)
                    .Select(folder => (Folder: folder, Files: JsonFiles(folder)))
                    .Where(p => p.Files.Count > 0)
                    .Select(p => (Path.GetFileName(p.Folder), PluginName(p.Files[0]) ?? Path.GetFileName(p.Folder)))
                    .OrderBy(p => p.Item1, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return [];
            }
        }

        private static List<string> JsonFiles(string folder) =>
            Directory.GetFiles(folder, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();

        private static JsonObject? ReadJson(string file) =>
            JsonNode.Parse(BoundedFile.Read(file, FileLimit)) as JsonObject;

        private static string? PluginName(string file)
        {
            try
            {
                return ReadJson(file)?["plugin_name"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        // Each file's contents: its plugin_name (or file name) as a heading, then its values by key, nested values indented
        private List<PluginLine> Read(string folder)
        {
            string path = Path.Combine(Folder, folder);
            if (!Directory.Exists(path))
                return [new PluginLine($"The folder of this plugin, {folder}, was not found. Open Plugins again to refresh the list.", 0, false, IsNote: true)];
            var lines = new List<PluginLine>();
            foreach (string file in JsonFiles(path))
            {
                try
                {
                    var json = ReadJson(file) ?? new JsonObject();
                    lines.Add(new PluginLine(json["plugin_name"]?.ToString() ?? Path.GetFileName(file), 0, true));
                    foreach (var (key, value) in json.Where(p => p.Key != "plugin_name").OrderBy(p => p.Key, StringComparer.Ordinal))
                        AddValue(lines, key, value, 0);
                }
                catch (Exception ex)
                {
                    lines.Add(new PluginLine($"Could not read {Path.GetFileName(file)}: {ex.Message}", 0, false, IsNote: true));
                }
            }
            return lines.Count > 0 ? lines : [new PluginLine("The plugin has no settings files.", 0, false, IsNote: true)];
        }

        private static void AddValue(List<PluginLine> lines, string key, JsonNode? value, int indent)
        {
            switch (value)
            {
                case JsonObject obj:
                    lines.Add(new PluginLine(key + ":", indent, false));
                    foreach (var (name, child) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                        AddValue(lines, name, child, indent + 1);
                    break;
                case JsonArray array:
                    lines.Add(new PluginLine(key + ":", indent, false));
                    for (int i = 0; i < array.Count; i++)
                        AddValue(lines, (i + 1).ToString(CultureInfo.InvariantCulture), array[i], indent + 1);
                    break;
                default:
                    lines.Add(new PluginLine($"{key}: {value?.ToString() ?? "null"}", indent, false));
                    break;
            }
        }
    }
}
