using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommandCenter.Services
{
    // Other: control bar archives that are not offered here (added by hand); choosing a bar moves them aside
    public enum BarKind { Original, Pro, Other }

    public sealed record BarPackage(BarKind Kind, string Resolution, string Url, string Sha256, long Size);

    // Control Bar Pro from TheSuperHackers/GeneralsControlBar (MIT), the only bar offered besides the game's own.
    // Each download is checked against a pinned SHA-256 and only the .big archives are taken from it; scripts in
    // the zip are never extracted. It changes the interface only, so it does not cause mismatches.
    public static class AddonService
    {
        private const string Repo = "https://github.com/TheSuperHackers/GeneralsControlBar/raw/main/";

        public static readonly BarPackage[] Packages =
        {
            new(BarKind.Pro, "1280x720", Repo + "ControlBarProZH/Release/ControlBarProZH_v1.2_1280x720.zip", "A49570D9DB4661BB6CB5082D15A9708F8E47A36C63A3D732F3CC3A26B31B2946", 1221677),
            new(BarKind.Pro, "1600x900", Repo + "ControlBarProZH/Release/ControlBarProZH_v1.2_1600x900.zip", "F97428CF730C03FE4A5D9ED9C7E68193D0599C71E2D48E7F14EBCD2721ED03D6", 1221677),
            new(BarKind.Pro, "1920x1080", Repo + "ControlBarProZH/Release/ControlBarProZH_v1.2_1920x1080.zip", "5B987CB864AAF1C55AC8F4CF8DBF28A9473E1233BBE34F05316BFEE5B7EB0836", 1221633),
            new(BarKind.Pro, "2560x1440", Repo + "ControlBarProZH/Release/ControlBarProZH_v1.2_2560x1440.zip", "E5021B5E76A7646AE027D3104B8DA5E2FCB104007FB82213D8A84205A2E1EDE9", 4984326),
            new(BarKind.Pro, "3840x2160", Repo + "ControlBarProZH/Release/ControlBarProZH_v1.2_3840x2160.zip", "66AFBD045A09B514578BC696C670E6B58654DB1740C44B9221DB191E1273C5E7", 4984359),
        };

        // Archives in the game folder that belong to a control bar add-on
        private static readonly Regex BarArchive = new(@"^\d{3}_ControlBar\w*\.big$", RegexOptions.IgnoreCase);

        public static List<string> InstalledFiles() =>
            Directory.EnumerateFiles(GamePaths.Game, "*.big").Where(f => BarArchive.IsMatch(Path.GetFileName(f))).ToList();

        public static (BarKind Kind, string? Resolution) Installed()
        {
            var names = InstalledFiles().Select(Path.GetFileName).OfType<string>().ToList();
            if (names.Count == 0)
                return (BarKind.Original, null);
            if (names.Any(n => !n.Contains("ControlBarPro", StringComparison.OrdinalIgnoreCase)))
                return (BarKind.Other, null);

            // "340_ControlBarPro<height>ZH.big" sets the resolution; the Art and Data archives are shared between sizes
            var height = names.Select(n => Regex.Match(n, @"ControlBarPro(\d+)ZH\.big$", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            return (BarKind.Pro, height switch { "720" => "1280x720", "900" => "1600x900", "1080" => "1920x1080", "1440" => "2560x1440", "2160" => "3840x2160", _ => null });
        }

        // The official Generals Online launcher installs Control Bar Pro too and lists the files it owns here. Like that
        // launcher, Command Center leaves files it did not install alone, so the two never undo each other's work.
        private static string GoControlBarState =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeneralsOnline", "control-bar-pro.json");

        // Bar archives in the game folder that the official launcher installed (read only)
        public static List<string> ManagedByGo()
        {
            try
            {
                if (!File.Exists(GoControlBarState))
                    return new();
                using var doc = JsonDocument.Parse(File.ReadAllText(GoControlBarState));
                var root = doc.RootElement;
                if (!root.TryGetProperty("InstallDirectory", out var folder) || folder.GetString() is not { } directory
                    || !Path.GetFullPath(directory).TrimEnd('\\').Equals(Path.GetFullPath(GamePaths.Game).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return new();
                if (!root.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array)
                    return new();
                return files.EnumerateArray().Select(f => f.GetString()).OfType<string>()
                    .Where(name => BarArchive.IsMatch(name) && File.Exists(Path.Combine(GamePaths.Game, name)))
                    .ToList();
            }
            catch
            {
                return new();
            }
        }

        // The Pro resolution that matches the game's own resolution in Options.ini
        public static string SuggestedResolution()
        {
            var value = OptionsFile.Get("Resolution")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int height = value?.Length == 2 && int.TryParse(value[1], out int h) ? h : 1080;
            return height switch { <= 720 => "1280x720", <= 900 => "1600x900", <= 1080 => "1920x1080", <= 1440 => "2560x1440", _ => "3840x2160" };
        }

        public static BarPackage? Package(BarKind kind, string? resolution) =>
            Packages.FirstOrDefault(p => p.Kind == kind && (kind != BarKind.Pro || p.Resolution == resolution));

        // Moves the current bar out (quarantine, so Undo can bring it back), then installs the chosen one
        public static async Task<List<BackupEntry>> ApplyAsync(BarKind kind, string? resolution, IProgress<string>? progress = null)
        {
            if (GameLauncher.IsGameRunning())
                throw new InvalidOperationException("Close the game first; it keeps its archives open.");
            if (ManagedByGo().Count > 0)
                throw new InvalidOperationException("This control bar was installed by the Generals Online launcher. Change or remove it there.");

            int before = BackupService.Load().Count;
            byte[]? zip = null;
            var package = kind == BarKind.Original ? null : Package(kind, resolution) ?? throw new InvalidOperationException("No package for that choice");

            // Download and check before touching anything in the game folder
            if (package != null)
            {
                progress?.Report($"Downloading {Views(package)}…");
                zip = await GoApi.Http.GetByteArrayAsync(package.Url);
                string hash = Convert.ToHexString(SHA256.HashData(zip));
                if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download does not match the expected checksum, so nothing was installed.");
            }

            progress?.Report("Moving the current control bar aside…");
            foreach (string file in InstalledFiles())
                BackupService.MoveToQuarantine("Control bar removed", file, Path.GetFileName(file));

            if (zip != null)
            {
                progress?.Report("Installing…");
                using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    string name = Path.GetFileName(entry.FullName);
                    if (!BarArchive.IsMatch(name))
                        continue;
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    BackupService.WriteFile("Control bar installed", Path.Combine(GamePaths.Game, name), buffer.ToArray(), $"{name} · {Views(package!)}");
                }
            }

            return BackupService.Load().Skip(before).ToList();
        }

        public static string Views(BarPackage p) => $"Control Bar Pro {p.Resolution}";

        public static void Undo(List<BackupEntry> made)
        {
            foreach (var entry in Enumerable.Reverse(made))
                BackupService.Undo(entry);
        }
    }
}
