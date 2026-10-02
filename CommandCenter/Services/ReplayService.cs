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

                if (Encoding.ASCII.GetString(reader.ReadBytes(6)) != "GENREP")
                    return null;

                uint start = reader.ReadUInt32();
                uint end = reader.ReadUInt32();
                reader.ReadUInt32();
                bool desync = reader.ReadByte() != 0;
                bool quit = reader.ReadByte() != 0;
                reader.ReadBytes(8);
                string title = ReadUnicode(reader);
                reader.ReadBytes(16);
                string version = ReadUnicode(reader);
                ReadUnicode(reader);
                reader.ReadBytes(12);
                string options = ReadUtf8(reader);

                int.TryParse(Field(options, "SC"), out int cash);
                return new ReplayEntry(
                    path,
                    title,
                    ParseMap(options),
                    MapPathOf(options),
                    ParsePlayers(options),
                    DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime,
                    TimeSpan.FromSeconds(end > start ? end - start : 0),
                    version.Replace("Version ", "", StringComparison.OrdinalIgnoreCase).Trim(),
                    cash,
                    Field(options, "SR") == "1",
                    desync,
                    quit,
                    stream.Length);
            }
            catch
            {
                return null;
            }
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
        private static List<ReplayPlayer> ParsePlayers(string options)
        {
            var players = new List<ReplayPlayer>();
            foreach (string slot in Field(options, "S").Split(':'))
            {
                if (slot.Length < 2)
                    continue;
                string[] f = slot[1..].Split(',');
                if (slot[0] == 'H' && f.Length >= 8)
                {
                    var (faction, general) = Template(f[5]);
                    players.Add(new ReplayPlayer(f[0], true, "", faction, general, Int(f[4]), Int(f[7])));
                }
                else if (slot[0] == 'C' && f.Length >= 5)
                {
                    string difficulty = f[0] switch { "E" => "Easy", "M" => "Medium", _ => "Hard" };
                    var (faction, general) = Template(f[2]);
                    players.Add(new ReplayPlayer($"{difficulty} Army", false, difficulty, faction, general, Int(f[1]), Int(f[4])));
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
