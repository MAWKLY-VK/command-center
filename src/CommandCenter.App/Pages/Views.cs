using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Imaging;

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

        // Status colours used across the pages that have not moved to the theme yet (as Settings' diagnostics)
        public static readonly SolidColorBrush Passed = Brush("#52D273");
        public static readonly SolidColorBrush Warning = Brush("#FFB020");
        public static readonly SolidColorBrush Problem = Brush("#FF6B6B");
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
            HealthStatus.Problem => "Problem",
            HealthStatus.Warning => "Warning",
            _ => "Passed",
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
            var image = ReplayService.PreviewFor(mapPath, mapName) is { } path ? Bitmaps.LoadTga(path) : null;
            lock (Previews)
                Previews[key] = image;
            return image;
        }

        public static string Ago(DateTime when)
        {
            var span = DateTime.Now - when;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return string.Format("{0} min ago", (int)span.TotalMinutes);
            if (span.TotalHours < 24) return string.Format("{0} h ago", (int)span.TotalHours);
            if (span.TotalDays < 2) return "yesterday";
            if (span.TotalDays < 7) return string.Format("{0} days ago", (int)span.TotalDays);
            return when.ToString("d MMM");
        }

        public static string Day(DateTime when)
        {
            if (when.Date == DateTime.Today) return "Today";
            if (when.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
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
                    ProcessRunner.Start(new StartRequest(StartKind.ShowInFolder, path))?.Dispose();
                else if (Directory.Exists(path))
                    ProcessRunner.Start(new StartRequest(StartKind.OpenFolder, path))?.Dispose();
            }
            catch { }
        }

        // The player picks the Zero Hour folder; it is remembered, and whatever depends on the game hears of it
        // (AppServices). Nothing in it is changed.
        public static bool ChooseGameFolder()
        {
            if (Main.ChooseFolder("Choose the Zero Hour folder", GamePaths.ZeroHourFound ? GamePaths.Game : null) is not { } folder)
                return false;
            if (!AppServices.GameFolder.Choose(folder))
            {
                Main.Warn("That folder does not hold Zero Hour. Choose the folder with WindowZH.big and INIZH.big in it.");
                return false;
            }
            return true;
        }

        public static void Open(string target)
        {
            try
            {
                if (target == "firewall")
                    ProcessRunner.Start(new StartRequest(StartKind.FirewallSettings))?.Dispose();
                else if (target.StartsWith("select:"))
                    ShowInFolder(target[7..]);
                else
                    ProcessRunner.Start(new StartRequest(StartKind.Shell, target))?.Dispose();
            }
            catch (Exception ex)
            {
                Main.Toast(string.Format("Could not open it: {0}", ex.Message), isError: true);
            }
        }

        // A plain-text summary for the Generals Online Discord; addresses and the Windows user and PC names are hidden
        public static void CopySupportReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Command Center support report");
            sb.AppendLine($"Windows {Environment.OSVersion.Version} · {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
            sb.AppendLine($"Client {AppState.ClientVersion()} · anti-cheat {GoSettings.Load().AntiCheat} · launcher {AppState.Version}");
            foreach (var h in AppState.Health.OrderByDescending(h => h.Status))
                sb.AppendLine($"[{h.Status}] {h.Title}: {h.Detail}");
            string text = Redaction.Report(sb.ToString());
            try
            {
                Clipboard.SetText(text);
                Main.Toast("Support report copied. Paste it in the Generals Online Discord.");
            }
            catch (Exception ex)
            {
                Main.Toast(string.Format("Could not copy: {0}", ex.Message), isError: true);
            }
        }

        // Runs a health fix, then offers Undo on the change it made
        public static void ApplyFix(HealthResult result)
        {
            if (result.Id.StartsWith("sample:"))
            {
                Main.Toast("This is a sample problem; nothing was changed.");
                return;
            }
            int before = BackupService.Load().Count;
            try
            {
                OperationLog.Record("fix", result.Id, result.Title, result.Fix!);
            }
            catch (Exception ex)
            {
                Main.Toast($"{result.Title}: {ex.Message}", isError: true);
                return;
            }
            var made = BackupService.Load().Skip(before).ToList();
            Main.Toast(string.Format("Fixed: {0}", result.Title), made.Count == 0 ? null : () =>
            {
                foreach (var entry in Enumerable.Reverse(made))
                    BackupService.Undo(entry);
                _ = AppState.RefreshHealthAsync();
                Main.Toast("Change undone");
            });
            _ = AppState.RefreshHealthAsync();
        }
    }
}
