using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CommandCenter.Services
{
    public enum Faction { Random, Observer, Usa, China, Gla, Other }

    public sealed record ReplayPlayer(string Name, bool IsHuman, string Difficulty, Faction Faction, string General, int Color, int Team)
    {
        public string Army => Faction switch
        {
            Faction.Random => "Random",
            Faction.Observer => "Observer",
            _ => General.Length > 0 ? $"{FactionName} · {General}" : FactionName,
        };

        public string FactionName => Faction switch { Faction.Usa => "USA", Faction.China => "China", Faction.Gla => "GLA", _ => "" };
    }

    public sealed record ReplayEntry(
        string FilePath, string Title, string Map, string MapPath, IReadOnlyList<ReplayPlayer> Players, DateTime Start, TimeSpan Length,
        string Version, int StartingCash, bool SuperweaponsOff, bool Desync, bool QuitEarly, long SizeBytes)
    {
        public string FileName => Path.GetFileName(FilePath);
        public bool IsArchived => FilePath.StartsWith(GamePaths.ArchivedReplays, StringComparison.OrdinalIgnoreCase);
        public bool IsLastMatch => FileName.Equals("00000000.rep", StringComparison.OrdinalIgnoreCase);
        public IEnumerable<ReplayPlayer> Fighters => Players.Where(p => p.Faction != Faction.Observer);

        // "1v1", "2v2", "FFA 4" or "Skirmish" when any opponent is the computer
        public string Mode
        {
            get
            {
                var fighters = Fighters.ToList();
                if (fighters.Any(p => !p.IsHuman))
                    return "Skirmish";
                if (fighters.Count <= 1)
                    return "Solo";
                var teams = fighters.GroupBy(p => p.Team < 0 ? -100 - fighters.IndexOf(p) : p.Team).Select(g => g.Count()).ToList();
                if (teams.Count == 2)
                    return $"{teams[0]}v{teams[1]}";
                return teams.All(t => t == 1) ? $"FFA {fighters.Count}" : string.Join("v", teams);
            }
        }
    }

    public enum LeaveKind { Surrendered, Left, Disconnected }

    public enum MatchResult { Won, Lost }

    // What the recorded commands say about one player. Same order as ReplayEntry.Players.
    // LeftAt is when the player gave up or left (game time); Leave says how, and is null when it
    // could not be told. Result is only filled in when the match outcome could be worked out.
    public sealed record ReplayPlayerResult(string Name, TimeSpan? LeftAt, LeaveKind? Leave, MatchResult? Result)
    {
        public bool HasLeft => LeftAt != null || Leave != null;

        // "Surrendered 15:27", "Left 18:52", "Disconnected", "Left or surrendered 20:08"; empty when still there
        public string LeaveText
        {
            get
            {
                if (!HasLeft)
                    return "";
                string what = Leave switch { LeaveKind.Surrendered => "Surrendered", LeaveKind.Left => "Left", LeaveKind.Disconnected => "Disconnected", _ => "Left or surrendered" };
                return LeftAt is { } at ? $"{what} {Clock(at)}" : what;
            }
        }

        public static string Clock(TimeSpan time) =>
            time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}" : $"{time.Minutes}:{time.Seconds:D2}";
    }

    // Recorder is the index of the player who recorded the file, or -1. Mapped is false when the commands
    // could not be matched to the players (then no player has leave or result information).
    // Complete is false when the recorded commands ended in the middle of a command.
    public sealed record ReplayResults(IReadOnlyList<ReplayPlayerResult> Players, TimeSpan Length, int Recorder, bool Mapped, bool Complete)
    {
        public bool HasWinner => Players.Any(p => p.Result == MatchResult.Won);
    }

    // Reads the game's replay files (Replays and the ArchivedReplays folder the game fills when
    // ArchiveReplays is on) and starts the game in replay mode.
    public static class ReplayService
    {
        public static string LastReplayPath => Path.Combine(GamePaths.Replays, "00000000.rep");

        // Order of PlayerTemplate.ini in Zero Hour; slot strings refer to these by index
        private static readonly (Faction Faction, string General)[] Templates =
        {
            (Faction.Other, "Civilian"), (Faction.Observer, ""), (Faction.Usa, ""), (Faction.China, ""), (Faction.Gla, ""),
            (Faction.Usa, "Superweapon"), (Faction.Usa, "Laser"), (Faction.Usa, "Air Force"),
            (Faction.China, "Tank"), (Faction.China, "Infantry"), (Faction.China, "Nuke"),
            (Faction.Gla, "Toxin"), (Faction.Gla, "Demolition"), (Faction.Gla, "Stealth"), (Faction.Other, "Boss"),
        };

        // MultiplayerColor order from Multiplayer.ini
        public static readonly string[] Colors = { "#DDE20D", "#FF0000", "#4368FE", "#3ED12E", "#FFA019", "#32D7E6", "#9600C8", "#FF96FF" };

        public static List<ReplayEntry> ScanAll()
        {
            var all = new List<ReplayEntry>();
            all.AddRange(Scan(GamePaths.Replays, recursive: true));
            all.AddRange(Scan(GamePaths.ArchivedReplays, recursive: false));
            return all.OrderByDescending(r => r.Start).ToList();
        }

        public static List<ReplayEntry> Scan(string folder, bool recursive)
        {
            var replays = new List<ReplayEntry>();
            if (!Directory.Exists(folder))
                return replays;

            foreach (string file in Directory.EnumerateFiles(folder, "*.rep", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                // Our own copies for watching live in a subfolder and are not listed twice
                if (file.Contains(@"\CommandCenter\", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (new FileInfo(file).Length == 0)
                    continue;
                var replay = Read(file);
                if (replay != null)
                    replays.Add(replay);
            }
            return replays;
        }

        // Header: "GENREP", start and end time, frame count, desync and quit flags, 8 disconnect flags,
        // replay name, SYSTEMTIME, version and build strings, version number, two CRCs, the game
        // options ("M=<map>;...;S=<slots>;") and the local slot index.
        public static ReplayEntry? Read(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);

                var header = ReadHeader(reader);
                if (header == null)
                    return null;

                string options = header.Options;
                int.TryParse(Field(options, "SC"), out int cash);
                return new ReplayEntry(
                    path,
                    header.Title,
                    ParseMap(options),
                    MapPathOf(options),
                    ParsePlayers(options),
                    DateTimeOffset.FromUnixTimeSeconds(header.Start).LocalDateTime,
                    TimeSpan.FromSeconds(header.End > header.Start ? header.End - header.Start : 0),
                    header.Version.Replace("Version ", "", StringComparison.OrdinalIgnoreCase).Trim(),
                    cash,
                    Field(options, "SR") == "1",
                    header.Desync,
                    header.Quit,
                    stream.Length);
            }
            catch
            {
                return null;
            }
        }

        private sealed record Header(uint Start, uint End, uint Frames, bool Desync, bool Quit, bool[] Disconnected, string Title, string Version, string Options);

        // Leaves the reader behind the game options string
        private static Header? ReadHeader(BinaryReader reader)
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(6)) != "GENREP")
                return null;

            uint start = reader.ReadUInt32();
            uint end = reader.ReadUInt32();
            uint frames = reader.ReadUInt32();
            bool desync = reader.ReadByte() != 0;
            bool quit = reader.ReadByte() != 0;
            bool[] disconnected = reader.ReadBytes(8).Select(b => b != 0).ToArray();
            string title = ReadUnicode(reader);
            reader.ReadBytes(16);
            string version = ReadUnicode(reader);
            ReadUnicode(reader);
            reader.ReadBytes(12);
            string options = ReadUtf8(reader);
            return new Header(start, end, frames, desync, quit, disconnected, title, version, options);
        }

        // "M=" holds a two character prefix, then "maps/<name>" or "userdata/maps/<name>".
        private static string ParseMap(string options)
        {
            string value = Field(options, "M");
            if (value.Length > 2)
                value = value[2..];

            string[] parts = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            int maps = Array.FindIndex(parts, p => p.Equals("maps", StringComparison.OrdinalIgnoreCase));
            string name = maps >= 0 && maps + 1 < parts.Length ? parts[maps + 1] : parts.LastOrDefault() ?? "";
            return name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }

        private static string MapPathOf(string options)
        {
            string value = Field(options, "M");
            return value.Length > 2 ? value[2..].Replace('\\', '/') : "";
        }

        // The game caches a preview of every map it has shown as MapPreviews\<path with '_'>.tga,
        // for example "maps/tournament desert" -> "maps_tournament desert_tournament desert.tga"
        public static string? PreviewFor(string mapPath, string mapName)
        {
            if (mapPath.Length > 0)
            {
                // Replays store only the map's folder ("maps/defcon6"); the file inside has the folder's name
                string trimmed = mapPath.TrimEnd('/');
                string stem = trimmed.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
                    ? trimmed[..^4]
                    : trimmed + "/" + trimmed[(trimmed.LastIndexOf('/') + 1)..];
                string cached = Path.Combine(GamePaths.UserData, "MapPreviews", stem.Replace('/', '_') + ".tga");
                if (File.Exists(cached))
                    return cached;
            }
            string user = Path.Combine(GamePaths.Maps, mapName, mapName + ".tga");
            return File.Exists(user) ? user : null;
        }

        // Human: H<name>,IP,port,TF,color,template,start,team,nat   Computer: C<E|M|H>,color,template,start,team
        private static List<ReplayPlayer> ParsePlayers(string options) =>
            ParseSlots(options).Select(s => s.Player).ToList();

        // Occupied slots in order, with their position among the 8 slots
        private static List<(int Slot, ReplayPlayer Player)> ParseSlots(string options)
        {
            var players = new List<(int, ReplayPlayer)>();
            string[] slots = Field(options, "S").Split(':');
            for (int i = 0; i < slots.Length; i++)
            {
                string slot = slots[i];
                if (slot.Length < 2)
                    continue;
                string[] f = slot[1..].Split(',');
                if (slot[0] == 'H' && f.Length >= 8)
                {
                    var (faction, general) = Template(f[5]);
                    players.Add((i, new ReplayPlayer(f[0], true, "", faction, general, Int(f[4]), Int(f[7]))));
                }
                else if (slot[0] == 'C' && f.Length >= 5)
                {
                    string difficulty = f[0] switch { "E" => "Easy", "M" => "Medium", _ => "Hard" };
                    var (faction, general) = Template(f[2]);
                    players.Add((i, new ReplayPlayer($"{difficulty} Army", false, difficulty, faction, general, Int(f[1]), Int(f[4]))));
                }
            }
            return players;
        }

        private static (Faction, string) Template(string value)
        {
            int index = Int(value);
            if (index == -2) return (Faction.Observer, "");
            if (index < 0 || index >= Templates.Length) return (Faction.Random, "");
            return Templates[index];
        }

        private static int Int(string s) => int.TryParse(s, out int v) ? v : -1;

        private static string Field(string options, string key)
        {
            foreach (string part in options.Split(';'))
            {
                if (part.StartsWith(key + "=", StringComparison.Ordinal))
                    return part[(key.Length + 1)..];
            }
            return "";
        }

        private static string ReadUnicode(BinaryReader reader)
        {
            var sb = new StringBuilder();
            for (char c = (char)reader.ReadUInt16(); c != '\0'; c = (char)reader.ReadUInt16())
                sb.Append(c);
            return sb.ToString();
        }

        private static string ReadUtf8(BinaryReader reader)
        {
            var bytes = new List<byte>();
            for (byte c = reader.ReadByte(); c != 0; c = reader.ReadByte())
                bytes.Add(c);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        // ── Results ──

        // After the header the file holds every command that went through the game's network layer, in order:
        // frame (u32), message type (i32), player index (i32), argument kind count (u8), per kind its type and
        // count (u8 each), then the arguments. The match is not stored, so what can be told comes from two
        // commands: MSG_SELF_DESTRUCT (1093), sent when a player surrenders, leaves through the menu or is
        // dropped for lag, and MSG_LOGIC_CRC (1095), which every connected player sends every few seconds.
        // A player whose CRCs go on after the self destruct stayed to watch (surrendered); one whose CRCs stop
        // there left. A player who is defeated in the game leaves no trace, so a winner is only named when every
        // opponent is a person who surrendered, left or dropped and the last of them gave up while the game was on.
        private const int LogicFps = 30;
        private const int MsgClearGameData = 27;
        private const int MsgSelfDestruct = 1093;
        private const int MaxPlayers = 16;

        // Payload of each argument type: integer, real, boolean, object id, drawable id, team id,
        // location, pixel, pixel region, timestamp, wide char
        private static readonly int[] ArgumentSizes = { 4, 4, 1, 4, 4, 4, 12, 8, 16, 4, 2 };

        private static readonly ConcurrentDictionary<string, (long Size, DateTime Written, ReplayResults? Results)> ResultCache = new(StringComparer.OrdinalIgnoreCase);

        private sealed class Activity
        {
            public uint Last;
            public readonly List<uint> SelfDestructs = new();
        }

        public static ReplayResults? ReadResults(ReplayEntry replay) => ReadResults(replay.FilePath);

        // Null when the file is not a replay. Never throws; cached until the file changes.
        public static ReplayResults? ReadResults(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 256L * 1024 * 1024)
                    return null;
                if (ResultCache.TryGetValue(path, out var hit) && hit.Size == info.Length && hit.Written == info.LastWriteTimeUtc)
                    return hit.Results;

                var results = ParseResults(File.ReadAllBytes(path));
                ResultCache[path] = (info.Length, info.LastWriteTimeUtc, results);
                return results;
            }
            catch
            {
                return null;
            }
        }

        private static ReplayResults? ParseResults(byte[] data)
        {
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            var header = ReadHeader(reader);
            if (header == null)
                return null;

            var slots = ParseSlots(header.Options);
            int.TryParse(ReadUtf8(reader), out int localSlot);
            reader.ReadBytes(16);   // difficulty, game mode, rank points, frame rate limit

            var activity = new Dictionary<int, Activity>();
            int recorder = -1;
            uint last = 0;
            bool complete = false;
            int p = (int)stream.Position;
            while (true)
            {
                if (p == data.Length)
                {
                    complete = true;
                    break;
                }
                if (p + 13 > data.Length)
                    break;

                uint frame = BitConverter.ToUInt32(data, p);
                int type = BitConverter.ToInt32(data, p + 4);
                int player = BitConverter.ToInt32(data, p + 8);
                int kinds = data[p + 12];
                int q = p + 13;
                if (frame < last || type < 0 || type >= 2100 || q + kinds * 2 > data.Length)
                    break;

                long bytes = 0;
                bool known = true;
                for (int i = 0; i < kinds && known; i++)
                {
                    int kind = data[q + i * 2];
                    known = kind < ArgumentSizes.Length;
                    if (known)
                        bytes += ArgumentSizes[kind] * data[q + i * 2 + 1];
                }
                q += kinds * 2;
                if (!known || q + bytes > data.Length)
                    break;

                if (player >= 1 && player < MaxPlayers)
                {
                    if (!activity.TryGetValue(player, out var a))
                        activity[player] = a = new Activity();
                    a.Last = frame;
                    if (type == MsgSelfDestruct)
                        a.SelfDestructs.Add(frame);
                }
                if (type == MsgClearGameData)
                    recorder = player;

                last = frame;
                p = q + (int)bytes;
            }

            int count = slots.Count;
            int localRank = slots.FindIndex(s => s.Slot == localSlot);
            int offset = FindOffset(slots, localRank, activity.Keys.ToHashSet(), recorder);

            int interval = Int(Field(header.Options, "C"));
            if (interval <= 0)
                interval = 100;

            var left = new bool[count];
            var leftFrame = new uint?[count];
            var how = new LeaveKind?[count];
            for (int r = 0; r < count && offset >= 0; r++)
            {
                var (slot, player) = slots[r];
                if (!player.IsHuman)
                    continue;

                activity.TryGetValue(offset + r, out var a);
                bool dropped = slot < header.Disconnected.Length && header.Disconnected[slot];
                uint? first = a != null && a.SelfDestructs.Count > 0 ? a.SelfDestructs[0] : null;
                if (!dropped && first == null)
                    continue;

                left[r] = true;
                if (dropped)
                {
                    how[r] = LeaveKind.Disconnected;
                    leftFrame[r] = first ?? a?.Last;
                }
                else
                {
                    uint at = first!.Value;
                    leftFrame[r] = at;
                    if (a!.Last > at + 10)
                        how[r] = LeaveKind.Surrendered;
                    else if (last >= at + interval + 30)
                        how[r] = LeaveKind.Left;
                    // otherwise the recording ended too soon after to tell the two apart
                }
            }

            var result = new MatchResult?[count];
            if (offset >= 0)
                InferResult(slots, left, leftFrame, how, activity, offset, result);

            // Same order as ReplayEntry.Players
            var players = new List<ReplayPlayerResult>();
            for (int r = 0; r < count; r++)
            {
                TimeSpan? at = leftFrame[r] is { } f ? TimeSpan.FromSeconds(f / (double)LogicFps) : null;
                players.Add(new ReplayPlayerResult(slots[r].Player.Name, at, how[r], result[r]));
            }

            uint frames = header.Frames > 0 ? header.Frames : last;
            return new ReplayResults(players, TimeSpan.FromSeconds(frames / (double)LogicFps), localRank, offset >= 0, complete);
        }

        // The command's player index of the n-th occupied slot is offset + n. The offset depends on how many
        // players the map defines itself (the neutral one, sometimes a civilian), so it is taken from who sent
        // commands: only people send them, so every index seen must belong to a human slot. -1 when unclear.
        private static int FindOffset(List<(int Slot, ReplayPlayer Player)> slots, int localRank, HashSet<int> seen, int recorder)
        {
            int count = slots.Count;
            var best = new List<int>();
            int bestScore = 0;
            for (int k = 1; k + count <= MaxPlayers; k++)
            {
                if (seen.Any(i => i < k || i >= k + count || !slots[i - k].Player.IsHuman))
                    continue;

                int score = Enumerable.Range(0, count).Count(r => slots[r].Player.IsHuman && seen.Contains(k + r));
                if (score > bestScore)
                {
                    best.Clear();
                    bestScore = score;
                }
                if (score == bestScore && score > 0)
                    best.Add(k);
            }

            if (best.Count > 1 && localRank >= 0)
                best = best.Where(k => recorder == k + localRank).ToList();
            return best.Count == 1 ? best[0] : -1;
        }

        // Names the winning side when every opponent is a person who is known to be gone and nobody can have
        // been left to fight on their side but the players who stayed (see the notes above)
        private static void InferResult(List<(int Slot, ReplayPlayer Player)> slots, bool[] left, uint?[] leftFrame, LeaveKind?[] how,
            Dictionary<int, Activity> activity, int offset, MatchResult?[] result)
        {
            var fighters = Enumerable.Range(0, slots.Count).Where(i => slots[i].Player.Faction != Faction.Observer).ToList();
            // A team of -1 means the player is on their own
            int Alliance(int i) => slots[i].Player.Team >= 0 ? slots[i].Player.Team : -1 - i;

            var sides = fighters.GroupBy(Alliance).ToList();
            if (sides.Count < 2)
                return;

            foreach (var side in sides)
            {
                var opponents = fighters.Where(i => Alliance(i) != side.Key).ToList();
                if (opponents.Any(i => !slots[i].Player.IsHuman || !left[i] || leftFrame[i] == null))
                    continue;

                // The last one to go gave up while the game was on (surrendered, or lost the connection);
                // leaving through the menu can also happen after the match is already decided
                uint decisive = opponents.Max(i => leftFrame[i]!.Value);
                if (opponents.Any(i => leftFrame[i] == decisive && how[i] is not (LeaveKind.Surrendered or LeaveKind.Disconnected)))
                    continue;

                bool stayed = side.Any(i => slots[i].Player.IsHuman && !left[i] && activity.TryGetValue(offset + i, out var a) && a.Last >= decisive);
                if (!stayed)
                    continue;

                foreach (int i in fighters)
                    result[i] = Alliance(i) == side.Key ? MatchResult.Won : MatchResult.Lost;
                return;
            }
        }

        // ── Archiving ──

        // The game's own option: with "ArchiveReplays = yes" in Options.ini it copies every match to ArchivedReplays.
        public static bool ArchiveEnabled => OptionsFile.Get("ArchiveReplays")?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;

        public static void SetArchive(bool on) =>
            OptionsFile.Write(new Dictionary<string, string> { ["ArchiveReplays"] = on ? "yes" : "no" }, "Replay archive " + (on ? "turned on" : "turned off"));

        // ── Watching ──

        // The game opens "-replay <file>" relative to its Replays folder, so archived and renamed files are
        // first copied to Replays\CommandCenter\. The game runs without anti-cheat for replays, as offline play does.
        public static void Watch(ReplayEntry replay)
        {
            string relative;
            if (replay.FilePath.StartsWith(GamePaths.Replays + "\\", StringComparison.OrdinalIgnoreCase)
                && replay.FileName.All(c => c < 128 && c != ' '))
            {
                relative = Path.GetRelativePath(GamePaths.Replays, replay.FilePath);
            }
            else
            {
                string folder = Path.Combine(GamePaths.Replays, "CommandCenter");
                Directory.CreateDirectory(folder);
                foreach (string old in Directory.EnumerateFiles(folder, "*.rep"))
                {
                    try { File.Delete(old); } catch { }
                }
                string copy = Path.Combine(folder, "watch.rep");
                File.Copy(replay.FilePath, copy, overwrite: true);
                relative = Path.Combine("CommandCenter", "watch.rep");
            }

            var info = new ProcessStartInfo(Path.Combine(GamePaths.Game, GamePaths.GameExe), $"-replay \"{relative}\"")
            {
                WorkingDirectory = GamePaths.Game,
                UseShellExecute = false,
            };
            Process.Start(info)?.Dispose();
        }

        public static string Rename(ReplayEntry replay, string newName)
        {
            string clean = string.Concat(newName.Trim().Split(Path.GetInvalidFileNameChars()));
            if (clean.Length == 0)
                throw new ArgumentException("The name is empty");
            string target = Path.Combine(Path.GetDirectoryName(replay.FilePath)!, clean + ".rep");
            if (File.Exists(target))
                throw new IOException("A replay with this name already exists");
            File.Move(replay.FilePath, target);
            return target;
        }

        // The game always writes the last match to 00000000.rep and overwrites it next time;
        // "Keep" copies it under a readable name so the next match does not replace it.
        public static string Keep(ReplayEntry replay)
        {
            string map = string.Concat(replay.Map.Split(Path.GetInvalidFileNameChars()));
            string baseName = $"{replay.Start:yyyy-MM-dd HHmm} {map}".Trim();
            string target = Path.Combine(GamePaths.Replays, baseName + ".rep");
            for (int n = 2; File.Exists(target); n++)
                target = Path.Combine(GamePaths.Replays, $"{baseName} ({n}).rep");
            File.Copy(replay.FilePath, target);
            return target;
        }
    }
}
