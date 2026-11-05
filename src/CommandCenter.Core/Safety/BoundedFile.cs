using System.IO;

namespace CommandCenter.Core.Safety
{
    // Reading a file that may be damaged or hostile: the whole file, but only when it is no larger than the limit, so no
    // file can make Command Center allocate more than it expects (spec phase 3)
    public static class BoundedFile
    {
        public static byte[] Read(string path, long limit)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > limit)
                throw new InvalidDataException($"It is larger than {Describe(limit)}.");
            var data = new byte[file.Length];
            file.ReadExactly(data);
            return data;
        }

        // "64 MB", "256 KB"
        public static string Describe(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";
    }
}
