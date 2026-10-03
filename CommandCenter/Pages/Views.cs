using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    // Small helpers shared by the pages: formatting, opening files and folders, health fixes with Undo.
    public static class Views
    {
        public static MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public static SolidColorBrush Brush(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
            brush.Freeze();
            return brush;
        }

        // Status colours used across the Tools pages (same as the Options page diagnostics)
        public static readonly SolidColorBrush Passed = Brush("#55CC55");
        public static readonly SolidColorBrush Warning = Brush("#FFAA00");
        public static readonly SolidColorBrush Problem = Brush("#FF4444");
        public static readonly SolidColorBrush Hint = Brush("#A7AECB");
        public static readonly SolidColorBrush Blue = Brush("#2980FF");
        public static readonly SolidColorBrush Gold = Brush("#FECD03");

        public static SolidColorBrush StatusBrush(HealthStatus status) => status switch
        {
            HealthStatus.Problem => Problem,
            HealthStatus.Warning => Warning,
            _ => Passed,
        };

        public static string StatusWord(HealthStatus status) => status switch
        {
            HealthStatus.Problem => Loc.T("Problem"),
            HealthStatus.Warning => Loc.T("Warning"),
            _ => Loc.T("Passed"),
        };

        public static Brush PlayerBrush(int color) =>
            color >= 0 && color < ReplayService.Colors.Length ? Brush(ReplayService.Colors[color]) : Hint;

        private static readonly Dictionary<string, BitmapSource?> Previews = new(StringComparer.OrdinalIgnoreCase);

        public static BitmapSource? MapPreview(string mapPath, string mapName)
        {
            string key = mapPath + "|" + mapName;
            lock (Previews)
            {
                if (Previews.TryGetValue(key, out var cached))
                    return cached;
            }
            var image = ReplayService.PreviewFor(mapPath, mapName) is { } path ? TgaImage.Load(path) : null;
            lock (Previews)
                Previews[key] = image;
            return image;
        }

        public static string Ago(DateTime when)
        {
            var span = DateTime.Now - when;
            if (span.TotalMinutes < 1) return Loc.T("just now");
            if (span.TotalMinutes < 60) return Loc.T("{0} min ago", (int)span.TotalMinutes);
            if (span.TotalHours < 24) return Loc.T("{0} h ago", (int)span.TotalHours);
            if (span.TotalDays < 2) return Loc.T("yesterday");
            if (span.TotalDays < 7) return Loc.T("{0} days ago", (int)span.TotalDays);
            return when.ToString("d MMM");
        }

        public static string Day(DateTime when)
        {
            if (when.Date == DateTime.Today) return Loc.T("Today");
            if (when.Date == DateTime.Today.AddDays(-1)) return Loc.T("Yesterday");
            return when.ToString("ddd d MMM");
        }

        public static string Length(TimeSpan span) => span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");

        public static string Size(long bytes) => bytes switch
        {
            >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
            >= 1024 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes} B",
        };

        public static void ShowInFolder(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
                else if (Directory.Exists(path))
                    Process.Start("explorer.exe", $"\"{path}\"")?.Dispose();
            }
            catch { }
        }

        public static void Open(string target)
        {
            try
            {
                if (target == "firewall")
                    Process.Start(new ProcessStartInfo("control.exe", "firewall.cpl") { UseShellExecute = true })?.Dispose();
                else if (target.StartsWith("select:"))
                    ShowInFolder(target[7..]);
                else
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Main.Toast(Loc.T("Could not open it: {0}", ex.Message), isError: true);
            }
        }

        // A plain-text summary for the Generals Online Discord; the Windows user and PC names are replaced
        public static void CopySupportReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Command Center support report");
            sb.AppendLine($"Windows {Environment.OSVersion.Version} · {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
            sb.AppendLine($"Client {AppState.ClientVersion()} · anti-cheat {GoSettings.Load().AntiCheat} · launcher {AppState.Version}");
            foreach (var h in AppState.Health.OrderByDescending(h => h.Status))
                sb.AppendLine($"[{h.Status}] {h.Title}: {h.Detail}");
            string text = sb.ToString()
                .Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase)
                .Replace(Environment.MachineName, "<pc>", StringComparison.OrdinalIgnoreCase);
            try
            {
                Clipboard.SetText(text);
                Main.Toast(Loc.T("Support report copied. Paste it in the Generals Online Discord."));
            }
            catch (Exception ex)
            {
                Main.Toast(Loc.T("Could not copy: {0}", ex.Message), isError: true);
            }
        }

        // Runs a health fix, then offers Undo on the change it made
        public static void ApplyFix(HealthResult result)
        {
            if (result.Id.StartsWith("sample:"))
            {
                Main.Toast(Loc.T("This is a sample problem; nothing was changed."));
                return;
            }
            int before = BackupService.Load().Count;
            try
            {
                result.Fix!();
            }
            catch (Exception ex)
            {
                Main.Toast($"{result.Title}: {ex.Message}", isError: true);
                return;
            }
            var made = BackupService.Load().Skip(before).ToList();
            Main.Toast(Loc.T("Fixed: {0}", result.Title), made.Count == 0 ? null : () =>
            {
                foreach (var entry in Enumerable.Reverse(made))
                    BackupService.Undo(entry);
                _ = AppState.RefreshHealthAsync();
                Main.Toast(Loc.T("Change undone"));
            });
            _ = AppState.RefreshHealthAsync();
        }
    }
}
