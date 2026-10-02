using System.IO;
using System.Text;

namespace CommandCenter.Services
{
    // Read-only access to the game's data the same way the game resolves it (FileSystem, Win32BIGFileSystem and
    // ArchiveFileSystem in the engine): a loose file in the game folder wins, otherwise the first archive that holds it.
    //  - Every .big under the game folder takes part, sub folders too (the Steam edition keeps Generals' own archives
    //    in ZH_Generals\), in path order as _stricmp sorts it; the duplicate Data\INI\INIZH.big is skipped.
    //  - Generals Online's community patch archive takes part when it is turned on in settings.json. It goes in by its
    //    file name, so an add-on with a lower number (200_..., 340_...) still wins over it.
    //  - Art\Textures: when TexturesZH.big and Generals' Textures.big both hold a picture, the bigger one wins.
    public sealed class GameFiles
    {
        private sealed record Entry(string Archive, uint Offset, uint Size);

        private readonly string _gameFolder;
        private readonly Dictionary<string, List<Entry>> _index = new(StringComparer.OrdinalIgnoreCase);

        public GameFiles(string gameFolder, bool? communityPatch = null)
        {
            _gameFolder = gameFolder;
            UsesCommunityPatch = communityPatch ?? GoSettings.Load().Get("data_packs", "use_community_data_patch", true);

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, MaxRecursionDepth = 16 };
            var archives = Directory.EnumerateFiles(gameFolder, "*.big", options)
                .Select(f => Path.GetRelativePath(gameFolder, f))
                .Where(r => !r.EndsWith(@"Data\INI\INIZH.big", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r, EngineOrder)
                .ToList();
            foreach (string archive in archives)
                AddArchive(Path.Combine(gameFolder, archive), sortedByName: false);

            string patch = Path.Combine(GamePaths.UserData, "GeneralsOnlineGameData", "500_900_CommunityPatch_CoreINI.big");
            if (UsesCommunityPatch && File.Exists(patch))
                AddArchive(patch, sortedByName: true);

            PrioritizeTexturesBySize();
        }

        public string GameFolder => _gameFolder;
        public bool UsesCommunityPatch { get; }

        // The order the game sorts names in (_stricmp: A-Z compared as a-z, everything else by code)
        public static IComparer<string> EngineOrder { get; } = Comparer<string>.Create(CompareNoCase);

        public static int CompareNoCase(string? a, string? b)
        {
            a ??= "";
            b ??= "";
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int x = Lower(a[i]), y = Lower(b[i]);
                if (x != y)
                    return x - y;
            }
            return a.Length - b.Length;
        }

        private static int Lower(char c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

        public string LoosePath(string relativePath) => Path.Combine(_gameFolder, relativePath);

        public bool Exists(string relativePath) =>
            File.Exists(LoosePath(relativePath)) || _index.ContainsKey(Normalize(relativePath));

        public bool ExistsInArchive(string relativePath) => _index.ContainsKey(Normalize(relativePath));

        // The archive the game takes this file from, or null
        public string? ArchiveOf(string relativePath) =>
            _index.TryGetValue(Normalize(relativePath), out var list) ? list[0].Archive : null;

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
            if (!_index.TryGetValue(Normalize(relativePath), out var list))
                return null;

            var entry = list[0];
            using var stream = File.OpenRead(entry.Archive);
            stream.Position = entry.Offset;
            byte[] data = new byte[entry.Size];
            stream.ReadExactly(data);
            return data;
        }

        // Archive entries plus loose files under the game folder, the way the game merges them
        public IEnumerable<string> List(string prefix)
        {
            prefix = Normalize(prefix);
            var names = new HashSet<string>(_index.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
            string looseFolder = LoosePath(prefix);
            if (Directory.Exists(looseFolder))
            {
                foreach (string file in Directory.EnumerateFiles(looseFolder, "*", SearchOption.AllDirectories))
                    names.Add(Path.GetRelativePath(_gameFolder, file));
            }
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }

        private static string Normalize(string path) => path.Replace('/', '\\');

        // A later archive goes behind the ones already holding a file. The community patch instead goes before the
        // first archive whose file name sorts after its own (ArchiveFileSystem::loadIntoDirectoryTree, sortedByName).
        private void AddArchive(string archive, bool sortedByName)
        {
            try
            {
                string name = Path.GetFileName(archive);
                foreach (var (file, offset, size) in ReadIndex(archive))
                {
                    var entry = new Entry(archive, offset, size);
                    string key = Normalize(file);
                    if (!_index.TryGetValue(key, out var list))
                    {
                        _index[key] = new List<Entry> { entry };
                        continue;
                    }
                    if (!sortedByName)
                    {
                        list.Add(entry);
                        continue;
                    }
                    int at = 0;
                    while (at < list.Count && CompareNoCase(Path.GetFileName(list[at].Archive), name) <= 0)
                        at++;
                    list.Insert(at, entry);
                }
            }
            catch
            {
                // Skip unreadable archives
            }
        }

        // W3DFileSystem::reprioritizeTexturesBySize: a picture in Art\Textures that TexturesZH.big holds first gives way
        // to a bigger copy in Generals' Textures.big
        private void PrioritizeTexturesBySize()
        {
            foreach (var (key, list) in _index)
            {
                if (list.Count < 2 || !key.StartsWith(@"Art\Textures\", StringComparison.OrdinalIgnoreCase) || key.IndexOf('\\', 13) >= 0
                    || !(key.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
                    continue;
                for (int i = 1; i < list.Count; i++)
                {
                    if (list[0].Archive.EndsWith("TexturesZH.big", StringComparison.OrdinalIgnoreCase)
                        && list[i].Archive.EndsWith("Textures.big", StringComparison.OrdinalIgnoreCase)
                        && list[0].Size < list[i].Size)
                        (list[0], list[i]) = (list[i], list[0]);
                }
            }
        }

        // BIG header: "BIGF", archive size (LE), file count (BE), first offset (BE),
        // then per file: offset (BE), size (BE), zero-terminated name. The game skips any other kind (BIG4).
        private static IEnumerable<(string Name, uint Offset, uint Size)> ReadIndex(string archive)
        {
            using var reader = new BinaryReader(File.OpenRead(archive));
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (magic != "BIGF")
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
