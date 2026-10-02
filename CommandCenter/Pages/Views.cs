using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    // Small building blocks shared by the pages.
    public static class Views
    {
        public static MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        public static Geometry Icon(string key) => (Geometry)Application.Current.FindResource("I." + key);
        public static Style Style(string key) => (Style)Application.Current.FindResource(key);

        public static Mark MarkOf(HealthStatus status) => status switch
        {
            HealthStatus.Problem => Mark.Danger,
            HealthStatus.Warning => Mark.Warn,
            _ => Mark.Ok,
        };

        public static Mark MarkOf(IssueLevel level) => level switch
        {
            IssueLevel.Error => Mark.Danger,
            IssueLevel.Warn => Mark.Warn,
            IssueLevel.Info => Mark.Info,
            _ => Mark.Ok,
        };

        public static string Word(HealthStatus status) => status switch
        {
            HealthStatus.Problem => "Problem",
            HealthStatus.Warning => "Warning",
            _ => "Passed",
        };

        public static Border Chip(string icon, string text, Mark mark = Mark.None)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (mark != Mark.None)
                row.Children.Add(new StatusMark { Mark = mark, Width = 13, Height = 13, Margin = new Thickness(0, 0, 8, 0) });
            else
                row.Children.Add(new Controls.Icon { Data = Icon(icon), Width = 15, Height = 15, Foreground = Res("IconIdle"), Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return new Border { Style = Style("Chip"), Child = row, Margin = new Thickness(0, 0, 10, 8) };
        }

        public static Border Tag(string text, Brush? foreground = null, Brush? border = null, Brush? background = null)
        {
            return new Border
            {
                Style = Style("Tag"),
                BorderBrush = border ?? Res("LineHi"),
                Background = background ?? Brushes.Transparent,
                Child = new TrackedText { Text = text, FontSize = 11.5, Tracking = 0.1, Foreground = foreground ?? Res("Text2"), VerticalAlignment = VerticalAlignment.Center },
            };
        }

        public static Brush PlayerBrush(int color)
        {
            if (color < 0 || color >= ReplayService.Colors.Length)
                return Res("Text3");
            var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(ReplayService.Colors[color])!;
            brush.Freeze();
            return brush;
        }

        // A player with a diamond in their in-game colour, trimmed with a tooltip when the name is long
        public static FrameworkElement Player(ReplayPlayer player, double maxWidth = 150, double fontSize = 14)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 9, Height = 9, Fill = PlayerBrush(player.Color), Margin = new Thickness(1, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45),
            });
            row.Children.Add(new TextBlock
            {
                Text = player.Name, FontSize = fontSize, FontWeight = FontWeights.SemiBold, MaxWidth = maxWidth,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            });
            row.ToolTip = $"{player.Name} · {player.Army}";
            return row;
        }

        private static readonly Dictionary<string, BitmapSource?> Previews = new(StringComparer.OrdinalIgnoreCase);

        public static BitmapSource? MapPreview(string mapPath, string mapName)
        {
            string key = mapPath + "|" + mapName;
            if (Previews.TryGetValue(key, out var cached))
                return cached;
            var image = ReplayService.PreviewFor(mapPath, mapName) is { } path ? TgaImage.Load(path) : null;
            Previews[key] = image;
            return image;
        }

        public static string Ago(DateTime when)
        {
            var span = DateTime.Now - when;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
            if (span.TotalDays < 2) return "yesterday";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays} days ago";
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
                Main.Toast("Could not open it: " + ex.Message, isError: true);
            }
        }

        // One line of a health check: shape and word, title, detail and the fix or guide button
        public static FrameworkElement HealthRow(HealthResult result, Action<HealthResult> fix, bool compact = false)
        {
            var grid = new Grid { Margin = compact ? new Thickness(0, 5, 0, 5) : new Thickness(0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var mark = new StatusMark { Mark = MarkOf(result.Status), Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
            mark.ToolTip = Word(result.Status);
            grid.Children.Add(mark);

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = result.Title, FontSize = compact ? 14 : 15, FontWeight = FontWeights.SemiBold, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap });
            if (!compact)
            {
                text.Children.Add(new TextBlock { Text = result.Detail, Style = Style("Body"), FontSize = 13, LineHeight = 18, Margin = new Thickness(0, 3, 0, 0) });
                if (result.Note != null)
                    text.Children.Add(new TextBlock { Text = result.Note, FontSize = 12.5, Foreground = Res("Text3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                if (result.FixPreview != null && result.Status != HealthStatus.Passed)
                {
                    var will = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                    var infoIcon = new Controls.Icon { Data = Icon("info"), Width = 13, Height = 13, Foreground = Res("Info"), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top };
                    DockPanel.SetDock(infoIcon, Dock.Left);
                    will.Children.Add(infoIcon);
                    will.Children.Add(new TextBlock { Text = result.FixPreview, FontSize = 12.5, Foreground = Res("Text2"), TextWrapping = TextWrapping.Wrap });
                    text.Children.Add(will);
                }
            }
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            Button? action = null;
            if (result.Fix != null && result.FixLabel != null && result.Status != HealthStatus.Passed)
            {
                action = new Button { Content = result.FixLabel, Style = Style("BtnXs"), ToolTip = result.FixPreview };
                Ui.SetVariant(action, Variant.Primary);
                action.Click += (_, _) => fix(result);
            }
            else if (result.GuideTarget != null && result.GuideLabel != null)
            {
                action = new Button { Content = result.GuideLabel, Style = Style("BtnXs") };
                Ui.SetIcon(action, Icon("external"));
                action.Click += (_, _) => Open(result.GuideTarget);
            }
            if (action != null)
            {
                action.VerticalAlignment = VerticalAlignment.Top;
                action.Margin = new Thickness(14, 0, 0, 0);
                Grid.SetColumn(action, 2);
                grid.Children.Add(action);
            }
            return grid;
        }

        // A plain-text summary for the Generals Online Discord; the Windows user and PC names are replaced
        public static void CopySupportReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Command Center support report");
            sb.AppendLine($"Windows {Environment.OSVersion.Version} · {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
            sb.AppendLine($"Client {AppState.ClientVersion()} · anti-cheat {GoSettings.Load().AntiCheat} · launcher {AppState.Version}");
            foreach (var h in AppState.Health.OrderByDescending(h => h.Status))
                sb.AppendLine($"[{Word(h.Status)}] {h.Title}: {h.Detail}");
            string text = sb.ToString()
                .Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase)
                .Replace(Environment.MachineName, "<pc>", StringComparison.OrdinalIgnoreCase);
            try
            {
                Clipboard.SetText(text);
                Main.Toast("Support report copied. Paste it in the Generals Online Discord.");
            }
            catch (Exception ex)
            {
                Main.Toast("Could not copy: " + ex.Message, isError: true);
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
                result.Fix!();
            }
            catch (Exception ex)
            {
                Main.Toast($"{result.Title}: {ex.Message}", isError: true);
                return;
            }
            var made = BackupService.Load().Skip(before).ToList();
            Main.Toast($"Fixed: {result.Title}", made.Count == 0 ? null : () =>
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
