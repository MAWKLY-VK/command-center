using CommandCenter.Services;

namespace CommandCenter.Pages.Tools
{
    public sealed record QuestionAction(string Label, string Icon, Action Run);

    public sealed record Question(string Title, string Answer, IReadOnlyList<QuestionAction> Actions);

    // What new players ask most in the Generals Online help channels, with the answer and the place that fixes it.
    // Each answer rests on Generals Online's own settings and messages or on what its helpers advise; the checks cover
    // the rest. Written in English and translated where shown.
    public static class CommonQuestions
    {
        public static IReadOnlyList<Question> All(Action runChecks)
        {
            var checks = new QuestionAction("Run the checks", "shield", runChecks);
            QuestionAction Options(string label, string part) => new(label, "sliders", () => Views.Main.ShowOptions(part));
            QuestionAction Link(string label, string url) => new(label, "external", () => Views.Open(url));

            var list = new List<Question>
            {
                // Generals Online's own hint on the setting: on for the unmodified game, off for a mod
                new("Rooms show greyed out and I cannot join them",
                    "Almost every Generals Online room uses the community data patch. When it is turned off, or its file is missing, your game does not match those rooms. Turn it on in Options › Data packs; the checks name a missing file. Turn it off only while you play a mod, because mods do not work with it.",
                    new[] { Options("Data packs", "datapacks"), checks }),

                // The setting is max_height_only_when_lobby_host in Generals Online's settings.json
                new("The camera does not zoom out as far as I set",
                    "Generals Online uses your maximum camera height only in rooms you host. In someone else's room, that room's host decides it, so the camera can stop lower than yours.",
                    new[] { Options("Camera", "camera") }),

                new("The connection drops, or online games load slowly",
                    "Two settings in Options › Network often help: set the HTTP version to 1.1, or turn on the alternative endpoints. Try one at a time. The diagnostics on the same page show your ping, loss and NAT type, and copy them for a help request.",
                    new[] { Options("Network", "network") }),

                new("The game does not start, or an anti-cheat error appears",
                    "Run the checks: they find missing game files, a missing anti-cheat plugin, an install path with non-English letters (\"Invalid game executable path\") and the \"Run as administrator\" flag (error 740). Generals Online has two anti-cheats, Easy Anti-Cheat and GeneralsOnline AntiCheat; if one does not start, choose the other in Options › Plugins. If the game still does not start, Play offers the official launcher.",
                    new[] { checks, Options("Plugins", "plugins") }),

                new("My antivirus removed a game file",
                    "Generals Online is not code-signed, so Windows Defender and other antivirus programs sometimes remove its files after an update. Restore the file from the antivirus quarantine and add the game folder as an exclusion, or install Generals Online again. The checks name any file that is missing.",
                    GamePaths.ZeroHourFound
                        ? new[] { new QuestionAction("Show game folder", "folder", () => Views.ShowInFolder(GamePaths.Game)), checks }
                        : new[] { checks }),

                new("Do I need GenTool?",
                    "No. Generals Online already has the fixes GenTool used to add, and it does not load GenTool's d3d8.dll. Other DirectX replacements can stop the anti-cheat; the checks list any in the game folder.",
                    new[] { checks }),

                new("A game ends with a mismatch",
                    "Everyone in a match needs the same game files. The checks find add-ons and changed archives that alter unit rules, and a community data patch that is off or missing. Generals Online updates itself when it starts, so start it once before you join a room.",
                    new[] { checks }),

                new("Where are my replays, and how do I keep one?",
                    "The game saves each match as the last replay and overwrites it with the next one. Open Replays to watch, rename or keep a match before you play again. A replay plays back correctly only on the game version it was recorded with, so an old one can go out of sync after Generals Online updates.",
                    new[] { new QuestionAction("Replays", "film", () => Views.Main.ShowTools("replays")) }),

                new("Where do I get the game?",
                    "Generals Online needs Zero Hour from Steam or the EA app. Install it, then install Generals Online from its website into the same folder. Command Center finds both by itself; if it does not, choose the folder in Options › Game folder.",
                    new[] { Link("Steam", "https://store.steampowered.com/app/" + GamePaths.SteamAppId + "/"), Link("Generals Online", HealthService.DownloadPage), Options("Game folder", "game") }),

                new("Where can I ask for help?",
                    "Copy the support report and paste it with your question in the Generals Online Discord. It lists what the checks found, without your Windows user name or PC name.",
                    new[] { new QuestionAction("Copy support report", "copy", Views.CopySupportReport) }),
            };

            // With the game or Generals Online missing, how to get them comes first
            if (!GamePaths.ZeroHourFound || !GamePaths.GameFound)
            {
                var start = list[^2];
                list.Remove(start);
                list.Insert(0, start);
            }
            return list;
        }
    }
}
