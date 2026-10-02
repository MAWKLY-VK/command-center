using System.IO;
using System.Text;

namespace CommandCenter.Services
{
    // Read-only access to the game's data the same way the game resolves it:
    // a loose file in the game folder wins, otherwise the first .big archive (by name) that holds it.
    // Generals Online's community patch archive takes part when it is turned on in settings.json.
    public sealed class GameFiles
    {
        private readonly string _gameFolder;
        private readonly Dictionary<string, (string Archive, uint Offset, uint Size)> _index = new(StringComparer.OrdinalIgnoreCase);

        public GameFiles(string gameFolder, bool? communityPatch = null)
        {
            _gameFolder = gameFolder;
            UsesCommunityPatch = communityPatch ?? GoSettings.Load().Get("data_packs", "use_community_data_patch", true);

            var archives = Directory.EnumerateFiles(gameFolder, "*.big").ToList();
            string patchFolder = Path.Combine(GamePaths.UserData, "GeneralsOnlineGameData");
            if (UsesCommunityPatch && Directory.Exists(patchFolder))
                archives.AddRange(Directory.EnumerateFiles(patchFolder, "*.big"));

            foreach (string archive in archives.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (var entry in ReadIndex(archive))
                        _index.TryAdd(entry.Name, (archive, entry.Offset, entry.Size));
                }
                catch
                {
                    // Skip unreadable archives
                }
            }
        }

        public string GameFolder => _gameFolder;
        public bool UsesCommunityPatch { get; }

        public string LoosePath(string relativePath) => Path.Combine(_gameFolder, relativePath);

        public bool Exists(string relativePath) =>
            File.Exists(LoosePath(relativePath)) || _index.ContainsKey(relativePath);

        public byte[]? Read(string relativePath)
        {
            string loose = LoosePath(relativePath);
            if (File.Exists(loose))
                return File.ReadAllBytes(loose);
            return ReadFromArchive(relativePath);
        }

        public string ReadText(string relativePath) =>
            Read(relativePath) is { } data ? Encoding.Latin1.GetString(data) : "";

        public byte[]? ReadFromArchive(string relativePath)
        {
            if (!_index.TryGetValue(relativePath, out var entry))
                return null;

            using var stream = File.OpenRead(entry.Archive);
            stream.Position = entry.Offset;
            byte[] data = new byte[entry.Size];
            stream.ReadExactly(data);
            return data;
        }

        // Archive entries plus loose files under the game folder, the way the game merges them
        public IEnumerable<string> List(string prefix)
        {
            var names = new HashSet<string>(_index.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
            string looseFolder = LoosePath(prefix);
            if (Directory.Exists(looseFolder))
            {
                foreach (string file in Directory.EnumerateFiles(looseFolder, "*", SearchOption.AllDirectories))
                    names.Add(Path.GetRelativePath(_gameFolder, file));
            }
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }

        // BIG header: "BIGF"/"BIG4", archive size (LE), file count (BE), first offset (BE),
        // then per file: offset (BE), size (BE), zero-terminated name.
        private static IEnumerable<(string Name, uint Offset, uint Size)> ReadIndex(string archive)
        {
            using var reader = new BinaryReader(File.OpenRead(archive));
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (magic != "BIGF" && magic != "BIG4")
                yield break;

            reader.ReadUInt32();
            uint count = ReadBigEndian(reader);
            reader.ReadUInt32();

            for (uint i = 0; i < count; i++)
            {
                uint offset = ReadBigEndian(reader);
                uint size = ReadBigEndian(reader);
                var name = new StringBuilder();
                for (byte c = reader.ReadByte(); c != 0; c = reader.ReadByte())
                    name.Append((char)c);
                yield return (name.ToString(), offset, size);
            }
        }

        private static uint ReadBigEndian(BinaryReader reader)
        {
            byte[] b = reader.ReadBytes(4);
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }
    }
}
