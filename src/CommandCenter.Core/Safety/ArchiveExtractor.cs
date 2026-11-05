using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace CommandCenter.Core.Safety
{
    // What one use takes out of a zip: the entry names it accepts, and the most one file may hold
    public sealed record ExtractRules(Func<string, bool> Accepts, long FileLimit);

    // Limits on a whole zip: its extracted bytes, its entries, and how far an entry over RatioFrom bytes may be compressed
    internal sealed record ZipLimits(long Total, int Entries, long RatioFrom, int Ratio)
    {
        public static readonly ZipLimits Default = new(1L << 30, 5000, 1L << 20, 200);
    }

    // Extracts zips from players and downloads (spec phase 3) into a new staging folder in the private temporary folder,
    // which the caller discards. A zip that is too large, holds too many entries, is compressed like a bomb or holds a
    // name that would leave the folder is refused, and the folder is removed. The bytes are counted while they are
    // copied, whatever the zip declares, so nothing beyond a limit is written.
    public static class ArchiveExtractor
    {
        // Lowered by the tests
        internal static ZipLimits Limits { get; set; } = ZipLimits.Default;

        public static string Extract(string zipPath, ExtractRules rules)
        {
            using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Extract(file, rules);
        }

        public static string Extract(Stream zip, ExtractRules rules)
        {
            var limits = Limits;
            if (zip.Length > limits.Total)
                throw new InvalidDataException($"The zip is larger than {BoundedFile.Describe(limits.Total)}.");
            if (DeclaredEntries(zip) > limits.Entries)
                throw new InvalidDataException($"The zip holds more than {limits.Entries.ToString("N0", CultureInfo.CurrentCulture)} files.");
            zip.Position = 0;

            string staging = DownloadService.NewTempPath("");
            Directory.CreateDirectory(staging);
            try
            {
                using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('/', '\\');
                    if (LeavesFolder(name) is { } leaves)
                        throw Refused(entry, leaves);
                    if (name.EndsWith('\\') || name.StartsWith(@"__MACOSX\", StringComparison.OrdinalIgnoreCase) || !rules.Accepts(name))
                        continue;
                    if (NameRefusal(name) is { } reason)
                        throw Refused(entry, reason);
                    if (entry.Length > rules.FileLimit)
                        throw TooLarge(entry, rules.FileLimit);

                    string destination = Path.Combine(staging, name);
                    if (File.Exists(destination))
                        throw Refused(entry, "it is in the zip twice");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    total += Copy(input, output, entry, rules.FileLimit, limits.Total - total, limits);
                }
                return staging;
            }
            catch
            {
                Discard(staging);
                throw;
            }
        }

        // Removes a staging folder and everything in it
        public static void Discard(string staging)
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // Left in the temporary folder, which Windows cleans
            }
        }

        // Why a name, in a zip or of a map, cannot be a file Command Center writes, or null. Beyond leaving the folder:
        // Windows device names (CON, NUL and others), names that are empty or only dots before their extension, and
        // parts that Windows would change (trailing dots or spaces, characters it does not allow).
        public static string? NameRefusal(string name)
        {
            if (LeavesFolder(name) is { } leaves)
                return leaves;
            foreach (string part in name.TrimEnd('\\').Split('\\'))
            {
                if (part.Length == 0)
                    return "it has an empty part";
                if (part.EndsWith('.') || part.EndsWith(' '))
                    return "it has a part that ends in a dot or a space";
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return "it has characters Windows does not allow";
                string stem = Path.GetFileNameWithoutExtension(part);
                if (stem.Length == 0)
                    return "it has no name before its extension";
                if (stem.Trim('.').Length == 0)
                    return "its name is only dots";
                if (DeviceNames.Contains(part.Split('.')[0].TrimEnd(' ')))
                    return "it is a Windows device name";
            }
            return null;
        }

        // A name that is rooted, names a drive or a network place, or steps up with ".."
        private static string? LeavesFolder(string name)
        {
            if (name.Length == 0)
                return "it has no name";
            if (name.StartsWith('\\') || Path.IsPathRooted(name))
                return "it is a full path";
            if (name.Contains(':'))
                return "it names a drive";
            if (name.Split('\\').Any(part => part == ".."))
                return "it leads out of the folder";
            return null;
        }

        private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        };

        // Copies an entry and returns its size. The count decides, not the sizes the zip declares, and a limit stops the
        // copy before the chunk that would pass it is written.
        private static long Copy(Stream input, Stream output, ZipArchiveEntry entry, long fileLimit, long totalLeft, ZipLimits limits)
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (written + read > fileLimit)
                    throw TooLarge(entry, fileLimit);
                if (written + read > totalLeft)
                    throw new InvalidDataException($"The zip's files add up to more than {BoundedFile.Describe(limits.Total)}.");
                if (written + read > limits.RatioFrom && written + read > Math.Max(1, entry.CompressedLength) * limits.Ratio)
                    throw new InvalidDataException($"\"{entry.FullName}\" is compressed more than {limits.Ratio} to 1, like a zip bomb.");
                output.Write(buffer, 0, read);
                written += read;
            }
            return written;
        }

        // The number of entries the zip's end record declares, read before .NET reads its whole directory (which then
        // has to hold the same number). A count too large for that record means a ZIP64 zip, more than any limit here.
        private static long DeclaredEntries(Stream zip)
        {
            const int RecordSize = 22;
            int tail = (int)Math.Min(zip.Length, RecordSize + ushort.MaxValue);
            if (tail < RecordSize)
                throw new InvalidDataException("It is not a zip file.");
            var buffer = new byte[tail];
            zip.Position = zip.Length - tail;
            zip.ReadExactly(buffer);
            for (int i = tail - RecordSize; i >= 0; i--)
            {
                if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
                {
                    int count = buffer[i + 10] | buffer[i + 11] << 8;
                    return count == ushort.MaxValue ? long.MaxValue : count;
                }
            }
            throw new InvalidDataException("It is not a zip file.");
        }

        private static InvalidDataException Refused(ZipArchiveEntry entry, string reason) =>
            new($"The zip holds \"{entry.FullName}\", which Command Center does not extract: {reason}.");

        private static InvalidDataException TooLarge(ZipArchiveEntry entry, long limit) =>
            new($"\"{entry.FullName}\" is larger than {BoundedFile.Describe(limit)}.");
    }
}
