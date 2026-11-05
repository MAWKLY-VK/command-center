using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CommandCenter.Core.Safety
{
    // What Command Center may start (spec phase 3)
    public enum StartKind
    {
        ShowInFolder,       // Explorer, with a file selected
        OpenFolder,         // Explorer, in a folder
        FirewallSettings,   // Windows Defender Firewall in the Control Panel
        Shell,              // an https page, Steam's file check or Windows Update, opened by Windows
        InGameFolder,       // a program in the game folder: the GO client, its anti-cheat launcher, the official launcher, the game
        Restart,            // Command Center itself, after an update
        AsAdministrator,    // Command Center or the previous version beside it, through Windows' prompt, for one update step
    }

    public sealed record StartRequest(StartKind Kind, string Target = "", IReadOnlyList<string>? Arguments = null);

    // The one way Command Center starts a program. Windows' own programs are named by their full path, programs in the
    // game folder only by a plain name, and arguments are passed as a list, so nothing can be slipped in through a path or
    // an argument.
    public static class ProcessRunner
    {
        private static readonly Regex ProgramName = new(@"^[A-Za-z0-9 _.\-]{1,64}\.exe$", RegexOptions.IgnoreCase);
        private static readonly Regex SteamCheck = new(@"^steam://validate/\d{1,10}$");
        private static readonly HashSet<string> WindowsPages = new(StringComparer.OrdinalIgnoreCase) { "ms-settings:windowsupdate" };

        // How a request starts, or null when Command Center does not start it
        internal static ProcessStartInfo? Prepare(StartRequest request)
        {
            string target = request.Target;
            var arguments = request.Arguments ?? [];
            switch (request.Kind)
            {
                case StartKind.ShowInFolder when Path.IsPathFullyQualified(target):
                    return Program(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), ["/select,", target]);
                case StartKind.OpenFolder when Path.IsPathFullyQualified(target):
                    return Program(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), [target]);
                case StartKind.FirewallSettings:
                    return Program(Path.Combine(Environment.SystemDirectory, "control.exe"), ["firewall.cpl"]);
                case StartKind.Shell when IsWebPage(target) || SteamCheck.IsMatch(target) || WindowsPages.Contains(target):
                    return new ProcessStartInfo(target) { UseShellExecute = true };
                case StartKind.InGameFolder when GamePaths.ZeroHourFound && ProgramName.IsMatch(target):
                    var info = Program(Path.Combine(GamePaths.Game, target), arguments);
                    info.WorkingDirectory = GamePaths.Game;
                    return info;
                case StartKind.Restart when Environment.ProcessPath is { } self:
                    var restart = Program(self, arguments);
                    restart.WorkingDirectory = Environment.CurrentDirectory;
                    return restart;
                case StartKind.AsAdministrator when IsUpdateStep(target, arguments):
                    // Windows' prompt goes through the shell, which takes the arguments as one line: each is quoted whole
                    return new ProcessStartInfo(target)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = string.Join(' ', arguments.Select(a => $"\"{a}\"")),
                        WorkingDirectory = Path.GetDirectoryName(target)!,
                    };
                default:
                    return null;
            }
        }

        // Starts what Prepare allows and returns the process, which the caller disposes
        public static Process? Start(StartRequest request)
        {
            var info = Prepare(request) ?? throw new InvalidOperationException($"Command Center does not start {Describe(request)}.");
            return Process.Start(info);
        }

        private static ProcessStartInfo Program(string path, IEnumerable<string> arguments)
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = false };
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
            return info;
        }

        // The only elevation in Command Center (P3-D1): this program replacing itself with a checked download, or the
        // previous version kept beside it putting itself back. No argument may hold a quote or end in a backslash, so the
        // quoting cannot be broken.
        private static bool IsUpdateStep(string target, IReadOnlyList<string> arguments)
        {
            if (Environment.ProcessPath is not { } self || !Path.IsPathFullyQualified(target) || arguments.Count == 0
                || arguments.Any(a => a.Length == 0 || a.Contains('"') || a.EndsWith('\\')))
                return false;
            return arguments[0] switch
            {
                UpdateService.ReplaceArgument => arguments.Count == 3 && SamePath(target, self),
                UpdateService.PutBackArgument => arguments.Count == 1 && SamePath(target, UpdateService.PreviousVersionOf(self)),
                _ => false,
            };
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static bool IsWebPage(string target) =>
            Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

        private static string Describe(StartRequest request) =>
            request.Target.Length > 0 ? $"\"{request.Target}\"" : request.Kind.ToString();
    }
}
