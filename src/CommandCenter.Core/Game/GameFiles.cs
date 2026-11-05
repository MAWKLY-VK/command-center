using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CommandCenter.Core.Game
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

            string patch = GamePaths.CommunityPatch;
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

        // The largest file Command Center reads from the game: a 32-bit texture at the texture limit, with room for its
        // header. A larger one is skipped and written to the error log.
        public const int ReadLimit = ImageLimits.Texture * ImageLimits.Texture * 4 + 1024 * 1024;

        public byte[]? Read(string relativePath)
        {
            string loose = LoosePath(relativePath);
            if (File.Exists(loose))
                return ReadLoose(loose);
            return ReadFromArchive(relativePath);
        }

        public string ReadText(string relativePath) =>
            Read(relativePath) is { } data ? Encoding.Latin1.GetString(data) : "";

        // A file on disk, or null when it is larger than the read limit
        public static byte[]? ReadLoose(string path)
        {
            try
            {
                return BoundedFile.Read(path, ReadLimit);
            }
            catch (InvalidDataException ex)
            {
                ErrorLog.Skipped(path, ex.Message);
                return null;
            }
        }

        public byte[]? ReadFromArchive(string relativePath)
        {
            if (!_index.TryGetValue(Normalize(relativePath), out var list))
                return null;

            var entry = list[0];
            if (entry.Size > ReadLimit)
            {
                ErrorLog.Skipped($"{relativePath} in {entry.Archive}", $"It is larger than {BoundedFile.Describe(ReadLimit)}.");
                return null;
            }
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
                var entries = ReadIndex(archive, out string? damage);
                if (damage != null)
                    ErrorLog.Skipped(archive, damage);
                foreach (var (file, offset, size) in entries)
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

        // A name in an archive's index is a path in the game folder, so no longer than a Windows path
        private const int MaxName = 260;

        // BIG header: "BIGF", archive size (LE), file count (BE), first offset (BE),
        // then per file: offset (BE), size (BE), zero-terminated name. The game skips any other kind (BIG4).
        // A damaged index keeps the entries that make sense: entries that lie outside the archive are left out, and a name
        // with no end stops the reading. damage then says what was wrong.
        private static List<(string Name, uint Offset, uint Size)> ReadIndex(string archive, out string? damage)
        {
            var entries = new List<(string Name, uint Offset, uint Size)>();
            damage = null;
            using var reader = new BinaryReader(File.OpenRead(archive));
            long length = reader.BaseStream.Length;
            if (length < 16 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "BIGF")
                return entries;

            reader.ReadUInt32();
            uint count = ReadBigEndian(reader);
            reader.ReadUInt32();

            int outside = 0;
            var name = new StringBuilder();
            try
            {
                for (uint i = 0; i < count && damage == null; i++)
                {
                    uint offset = ReadBigEndian(reader);
                    uint size = ReadBigEndian(reader);
                    name.Clear();
                    for (byte c = reader.ReadByte(); c != 0 && damage == null; c = reader.ReadByte())
                    {
                        if (name.Length == MaxName)
                            damage = $"A name in its index runs past {MaxName} characters, so the rest of the index was left out.";
                        else
                            name.Append((char)c);
                    }
                    if (damage != null)
                        break;
                    if ((long)offset + size > length)
                        outside++;
                    else
                        entries.Add((name.ToString(), offset, size));
                }
            }
            catch (EndOfStreamException)
            {
                damage = "Its index ends early, so its last entries were left out.";
            }
            if (outside > 0)
                damage = English.Plural(outside, "{0} entry of its index lies outside the archive and was left out.",
                    "{0} entries of its index lie outside the archive and were left out.") + (damage != null ? " " + damage : "");
            return entries;
        }

        private static uint ReadBigEndian(BinaryReader reader) => BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
    }
}
