using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CommandCenter.Services
{
    // Action is the CommandButton's "Command =" value, for example UNIT_BUILD or STOP
    public sealed record HotkeyButton(string Command, string Label, int Slot, string Name, string? Image, string Action = "");

    public enum MenuKind { Structure, Unit, Powers }

    // Buttons holds the ones that can have a hotkey (a TextLabel in generals.csf), with or without one by default;
    // Silent the other buttons on the same bar (science purchases without a label...)
    public sealed record HotkeyMenu(string Name, MenuKind Kind, string SetName, IReadOnlyList<HotkeyButton> Buttons, IReadOnlyList<HotkeyButton> Silent)
    {
        public int MaxSlot => Buttons.Concat(Silent).Select(b => b.Slot).DefaultIfEmpty(0).Max();
    }

    public sealed record HotkeyArmy(string Name, string Faction, string? General, IReadOnlyList<HotkeyMenu> Menus);

    public enum IssueLevel { Ok, Info, Warn, Error }

    public sealed record KeyIssue(IssueLevel Level, string Text);

    public sealed class GameKey
    {
        public required string Command { get; init; }
        public required string Name { get; init; }
        public required string Category { get; init; }
        public required int Block { get; init; }          // line of the block in the CommandMap file, -1 when built in
        public required string OriginalKey { get; init; }
        public required string OriginalModifiers { get; init; }
        public required string Transition { get; init; }
        public required string UseableIn { get; init; }
        public bool IsBuiltIn { get; init; }
        public bool IsTeam { get; init; }                 // always on the game's default; only reserved for conflict checks
        public bool IsFixed { get; init; }                // built in, and no CommandMap block can change it
        public string BuiltInCategory { get; init; } = "";
        public int Order { get; init; }
        public string FileKey { get; set; } = "";
        public string FileModifiers { get; set; } = "NONE";
        public string Key { get; set; } = "";
        public string Modifiers { get; set; } = "NONE";
        public bool Changed => Key != OriginalKey || Modifiers != OriginalModifiers;
        public bool Pending => Key != FileKey || Modifiers != FileModifiers;
        public bool CanChange => !IsTeam && !IsFixed;
        public string Shortcut => HotkeyService.Describe(Key, Modifiers);
        public string DefaultShortcut => HotkeyService.Describe(OriginalKey, OriginalModifiers);
    }

    // Reads every button hotkey (the '&' letter in generals.csf) and every global key (CommandMap.ini) from the
    // game data, checks them against the rules the engine applies, and writes changes as loose files that
    // override the archives. Neither file is part of the multiplayer check, so hotkeys never cause a mismatch.
    //
    // Engine rules (GeneralsMD HotKey.cpp, MetaEvent.cpp, ControlBar.cpp):
    //  - A button key is one letter or digit pressed alone; Ctrl, Alt and Shift are ignored for buttons.
    //  - Any button with a TextLabel gets the letter after the first '&' in that text as its key, so a button the
    //    game ships without a key (Sell, Exit) gets one when '&' is added. The tooltip draws "&X" as a highlighted X.
    //  - Two buttons with the same letter in one menu: only the first one works.
    //  - A plain global key (for example S for Stop) fires on key down and the button fires on key up, so both happen.
    //  - Two global commands on the same combination: only one of them works.
    //  - Some keys are built into the game when CommandMap.ini does not set them (Ctrl+I, F12, camera pitch...).
    //    A CommandMap block with a key stops the game from adding its own. Only Generals Online knows these command
    //    names; the original game stops at startup on a name it does not know, so they are written to a file in
    //    Data\<language>\CommandMap\, a folder that Generals Online reads after CommandMap.ini and the original game never opens.
    //  - Team keys (0-9 alone, with Ctrl, Shift or Alt) always stay on the game's defaults.
    public sealed class HotkeyService
    {
        public string Language { get; }
        public string CsfPath => $@"Data\{Language}\generals.csf";
        public string CommandMapPath => $@"Data\{Language}\CommandMap.ini";
        public string BuiltInMapPath => $@"Data\{Language}\CommandMap\CommandCenter.ini";

        private static readonly (string Side, string Name, string Faction, string? General, string? Prefix)[] ArmyDefs =
        {
            ("America", "USA", "USA", null, null),
            ("AmericaAirForceGeneral", "Air Force General", "USA", "Air Force", "AirF"),
            ("AmericaLaserGeneral", "Laser General", "USA", "Laser", "Lazr"),
            ("AmericaSuperWeaponGeneral", "Superweapon General", "USA", "Superweapon", "SupW"),
            ("China", "China", "China", null, null),
            ("ChinaTankGeneral", "Tank General", "China", "Tank", "Tank"),
            ("ChinaInfantryGeneral", "Infantry General", "China", "Infantry", "Infa"),
            ("ChinaNukeGeneral", "Nuke General", "China", "Nuke", "Nuke"),
            ("GLA", "GLA", "GLA", null, null),
            ("GLADemolitionGeneral", "Demolition General", "GLA", "Demolition", "Demo"),
            ("GLAStealthGeneral", "Stealth General", "GLA", "Stealth", "Slth"),
            ("GLAToxinGeneral", "Toxin General", "GLA", "Toxin", "Chem"),
        };

        // Names for commands that have no text of their own in the game, or share one with another command
        private static readonly Dictionary<string, string> CommandNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SELECT_NEXT_WORKER"] = "Select next worker",
            ["SELECT_PREV_WORKER"] = "Select previous worker",
            ["CHAT_ALLIES"] = "Chat to allies",
            ["CHAT_EVERYONE"] = "Chat to everyone",
            ["DIPLOMACY"] = "Diplomacy",
            ["PLACE_BEACON"] = "Place beacon",
            ["DELETE_BEACON"] = "Delete beacon",
            ["BEGIN_CAMERA_ROTATE_LEFT"] = "Rotate camera left",
            ["BEGIN_CAMERA_ROTATE_RIGHT"] = "Rotate camera right",
            ["BEGIN_CAMERA_ZOOM_IN"] = "Zoom in",
            ["BEGIN_CAMERA_ZOOM_OUT"] = "Zoom out",
            ["CAMERA_RESET"] = "Reset camera",
            ["TOGGLE_FAST_FORWARD_REPLAY"] = "Fast forward (replays)",
        };

        private static readonly HashSet<string> HiddenCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "BEGIN_FORCEATTACK", "BEGIN_WAYPOINTS", "BEGIN_PREFER_SELECTION", "TOGGLE_CAMERA_TRACKING_DRAWABLE",
        };

        // Keys the game adds by itself when CommandMap.ini does not define the command (MetaMap::generateMetaMap).
        // Category is the one the game gives them (it leaves MISC on all but the idle worker).
        private static readonly (string Command, string Name, string Key, string Mods, string Usable, string Category)[] BuiltIns =
        {
            ("SELECT_NEXT_IDLE_WORKER", "Next idle worker", "KEY_I", "CTRL", "GAME", "SELECTION"),
            ("TAKE_SCREENSHOT_PNG", "Screenshot (PNG)", "KEY_F12", "CTRL", "EVERYWHERE", "MISC"),
            ("INCREASE_MAX_RENDER_FPS", "Raise frame limit", "KEY_KPPLUS", "CTRL", "EVERYWHERE", "MISC"),
            ("DECREASE_MAX_RENDER_FPS", "Lower frame limit", "KEY_KPMINUS", "CTRL", "EVERYWHERE", "MISC"),
            ("INCREASE_LOGIC_TIME_SCALE", "Faster game speed (offline)", "KEY_KPPLUS", "SHIFT_CTRL", "EVERYWHERE", "MISC"),
            ("DECREASE_LOGIC_TIME_SCALE", "Slower game speed (offline)", "KEY_KPMINUS", "SHIFT_CTRL", "EVERYWHERE", "MISC"),
            ("TOGGLE_PAUSE_ALT", "Pause (replays and observers)", "KEY_P", "SHIFT", "EVERYWHERE", "MISC"),
            ("STEP_FRAME_ALT", "Step one frame (paused)", "KEY_O", "SHIFT", "EVERYWHERE", "MISC"),
            ("TOGGLE_PAUSE", "Pause (observer)", "KEY_P", "NONE", "OBSERVER", "MISC"),
            ("STEP_FRAME", "Step one frame (observer)", "KEY_O", "NONE", "OBSERVER", "MISC"),
            ("TOGGLE_PLAYER_OBSERVER", "Switch player view (observer)", "KEY_M", "NONE", "OBSERVER", "MISC"),
            ("ALT_CAMERA_ROTATE_LEFT", "Rotate camera left (alternate)", "KEY_KP4", "CTRL", "GAME", "MISC"),
            ("ALT_CAMERA_ROTATE_RIGHT", "Rotate camera right (alternate)", "KEY_KP6", "CTRL", "GAME", "MISC"),
            ("BEGIN_CAMERA_PITCH_UP", "Tilt camera up", "KEY_KP9", "NONE", "GAME", "MISC"),
            ("ALT_BEGIN_CAMERA_PITCH_UP", "Tilt camera up (alternate)", "KEY_PGUP", "NONE", "GAME", "MISC"),
            ("BEGIN_CAMERA_PITCH_DOWN", "Tilt camera down", "KEY_KP3", "NONE", "GAME", "MISC"),
            ("ALT_BEGIN_CAMERA_PITCH_DOWN", "Tilt camera down (alternate)", "KEY_PGDN", "NONE", "GAME", "MISC"),
            ("CAMERA_PITCH_RESET", "Reset camera tilt", "KEY_KP7", "NONE", "GAME", "MISC"),
            ("ALT_CAMERA_PITCH_RESET", "Reset camera tilt (alternate)", "KEY_HOME", "NONE", "GAME", "MISC"),
            ("TAKE_SCREENSHOT", "Screenshot", "KEY_F12", "NONE", "EVERYWHERE", "MISC"),
            ("INCREASE_OBSERVER_NOTIFICATION_FONT", "Larger observer messages", "KEY_RIGHT", "SHIFT", "GAME", "MISC"),
            ("DECREASE_OBSERVER_NOTIFICATION_FONT", "Smaller observer messages", "KEY_LEFT", "SHIFT", "GAME", "MISC"),
            ("INCREASE_OBSERVER_STATS_FONT", "Larger observer statistics", "KEY_UP", "SHIFT", "GAME", "MISC"),
            ("DECREASE_OBSERVER_STATS_FONT", "Smaller observer statistics", "KEY_DOWN", "SHIFT", "GAME", "MISC"),
        };

        // Built in, but missing from the CommandMap parser's name table: a block for them stops the game at startup
        private static readonly HashSet<string> FixedBuiltIns = new(StringComparer.OrdinalIgnoreCase)
        {
            "ALT_CAMERA_ROTATE_LEFT", "ALT_CAMERA_ROTATE_RIGHT",
        };

        // Keys the original game's CommandMap parser does not know (Generals Online added them)
        private static readonly HashSet<string> NewKeyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "KEY_KPPLUS", "KEY_KPMINUS", "KEY_KPDEL", "KEY_KPSTAR", "KEY_KPENTER",
        };

        private static readonly Regex TeamCommand = new(@"^(CREATE|SELECT|ADD|VIEW)_TEAM\d$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly GameFiles _files;
        private CsfFile _csf = null!;
        private CsfFile _originalCsf = null!;
        private string[] _commandMapLines = Array.Empty<string>();
        private readonly Dictionary<string, char> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);

        public GameImages Images { get; }
        public List<HotkeyArmy> Armies { get; } = new();
        public List<GameKey> GameKeys { get; } = new();
        public bool UsesCommunityPatch => _files.UsesCommunityPatch;
        public bool HasUnsavedChanges => _pendingKeys.Any(p => p.Value != SavedKeyFor(p.Key)) || GameKeys.Any(k => k.Pending);
        public int PendingCount => _pendingKeys.Count(p => p.Value != SavedKeyFor(p.Key)) + GameKeys.Count(k => k.Pending);

        // Team keys found changed in CommandMap.ini; they stay pending until Save puts the defaults back
        public int ChangedTeamKeys => GameKeys.Count(k => k.IsTeam && k.Pending);

        // Changes Discard can undo (everything pending except the team key repair)
        public int DiscardableCount => PendingCount - ChangedTeamKeys;

        // The game keys the editor lists: everything but the team keys
        public IEnumerable<GameKey> ListedGameKeys => GameKeys.Where(k => !k.IsTeam);

        private HotkeyService(GameFiles files, string language)
        {
            _files = files;
            Language = language;
            Images = new GameImages(files, language);
        }

        public static HotkeyService Load(string gameFolder)
        {
            var service = new HotkeyService(new GameFiles(gameFolder), DetectLanguage());
            service.Reload();
            return service;
        }

        // The game reads its language from the registry and falls back to English
        private static string DetectLanguage()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = hive.OpenSubKey(@"SOFTWARE\Electronic Arts\EA Games\Command and Conquer Generals Zero Hour");
                    if (key?.GetValue("Language") is string language && language.Length > 0)
                        return char.ToUpperInvariant(language[0]) + language[1..].ToLowerInvariant();
                }
                catch { }
            }
            return "English";
        }

        public void Reload()
        {
            _pendingKeys.Clear();
            _originalCsf = CsfFile.Load(_files.ReadFromArchive(CsfPath) ?? throw new FileNotFoundException(CsfPath));
            _csf = CsfFile.Load(_files.Read(CsfPath)!);
            LoadArmies();
            LoadGameKeys();
        }

        // ── Buttons ──

        private char SavedKeyFor(string label) => CsfFile.HotkeyOf(_csf.Get(label));

        public char KeyFor(string label) => _pendingKeys.TryGetValue(label, out char key) ? key : SavedKeyFor(label);

        public char DefaultKeyFor(string label) => CsfFile.HotkeyOf(_originalCsf.Get(label));

        public bool IsChanged(string label) => KeyFor(label) != DefaultKeyFor(label);

        public void SetKey(string label, char key) => _pendingKeys[label] = char.ToUpperInvariant(key);

        public void ResetKey(string label) => _pendingKeys[label] = DefaultKeyFor(label);

        // Team keys keep their defaults, so a repair stays pending
        public void DiscardPending()
        {
            _pendingKeys.Clear();
            foreach (var k in GameKeys.Where(k => !k.IsTeam))
            {
                k.Key = k.FileKey;
                k.Modifiers = k.FileModifiers;
            }
        }

        public static bool IsValidButtonKey(char c) => c is >= 'A' and <= 'Z' or >= '0' and <= '9';

        public IEnumerable<(HotkeyArmy Army, HotkeyMenu Menu)> MenusUsing(string label) =>
            Armies.SelectMany(a => a.Menus.Where(m => m.Buttons.Any(b => b.Label.Equals(label, StringComparison.OrdinalIgnoreCase))).Select(m => (a, m)));

        // Global commands on a plain letter or digit while playing (team keys included: plain digits select teams);
        // they fire together with a button on the same key
        public GameKey? PlainGlobalKey(char key)
        {
            string name = "KEY_" + char.ToUpperInvariant(key);
            return GameKeys.FirstOrDefault(k => k.Key == name && k.Modifiers == "NONE" && UsableInGame(k.UseableIn)
                                                && !k.Command.Equals("TOGGLE_FAST_FORWARD_REPLAY", StringComparison.OrdinalIgnoreCase));
        }

        private static bool UsableInGame(string useableIn) =>
            useableIn.Contains("GAME", StringComparison.OrdinalIgnoreCase) || useableIn.Contains("EVERYWHERE", StringComparison.OrdinalIgnoreCase);

        // The plain game key that fires together with this button, unless both do the same thing (the Stop button on S)
        public GameKey? SharedGameKey(HotkeyButton button)
        {
            char key = KeyFor(button.Label);
            var global = key == '\0' ? null : PlainGlobalKey(key);
            return global != null && !global.Command.Equals(button.Action, StringComparison.OrdinalIgnoreCase) ? global : null;
        }

        public List<KeyIssue> ButtonIssues(HotkeyMenu menu, HotkeyButton button)
        {
            var issues = new List<KeyIssue>();
            char key = KeyFor(button.Label);
            if (key == '\0')
                return issues;

            var first = menu.Buttons.First(b => KeyFor(b.Label) == key);
            var others = menu.Buttons.Where(b => b != button && b.Label != button.Label && KeyFor(b.Label) == key).ToList();
            if (others.Count > 0)
            {
                string names = string.Join(Loc.T(", "), others.Select(o => o.Name));
                issues.Add(first == button
                    ? new KeyIssue(IssueLevel.Error, Loc.T("{0} also uses {1} in this menu, so that button stops working.", names, key))
                    : new KeyIssue(IssueLevel.Error, Loc.T("{0} already uses {1} in this menu; the game only runs the first one.", first.Name, key)));
            }

            if (SharedGameKey(button) is { } global)
                issues.Add(new KeyIssue(IssueLevel.Warn, Loc.T("{0} is also the game key for {1}. Pressing {0} does both.", key, global.Name)));

            if (key == 'F')
                issues.Add(new KeyIssue(IssueLevel.Info, Loc.T("In replays F also turns fast forward on and off.")));

            // WithHotkey appends " (X)" when the name has no such letter; the tooltip shows it that way
            if (!CsfFile.WithoutHotkey(_originalCsf.Get(button.Label) ?? "").Contains(key, StringComparison.OrdinalIgnoreCase))
                issues.Add(new KeyIssue(IssueLevel.Info, Loc.T("The name has no {0}, so the game shows it as \"{1}\".", key, ShownName(button.Label))));

            if (!issues.Any(i => i.Level is IssueLevel.Error or IssueLevel.Warn))
                issues.Insert(0, new KeyIssue(IssueLevel.Ok, Loc.T("No other button in this menu uses {0}.", key)));
            return issues;
        }

        // The button's name the way the game's tooltip shows it with the current key (the '&' marker removed)
        public string ShownName(string label)
        {
            string original = _originalCsf.Get(label) ?? label;
            return CsfFile.WithoutHotkey(LabelText(original, KeyFor(label))).Replace("&&", "&");
        }

        private static string LabelText(string original, char key) =>
            key == CsfFile.HotkeyOf(original) ? original
            : key == '\0' ? CsfFile.WithoutHotkey(original)
            : CsfFile.WithHotkey(original, key);

        public IssueLevel WorstIssue(HotkeyMenu menu, HotkeyButton button) =>
            ButtonIssues(menu, button).Select(i => i.Level).DefaultIfEmpty(IssueLevel.Ok).Max();

        // Free letters for a button: not used in its menu and not a plain game key; letters of its name first
        public List<char> SuggestKeys(HotkeyMenu menu, HotkeyButton button, int count = 6)
        {
            var used = menu.Buttons.Select(b => KeyFor(b.Label)).ToHashSet();
            var free = Enumerable.Range('A', 26).Select(c => (char)c).Where(c => !used.Contains(c) && PlainGlobalKey(c) == null && c != 'F').ToList();
            string name = button.Name.ToUpperInvariant();
            return free.OrderBy(c => name.IndexOf(c) is int i && i >= 0 ? i : 100 + c).Take(count).ToList();
        }

        // Everything in an army that answers to a key: buttons per menu and plain game keys
        public List<string> WhoUses(HotkeyArmy army, char key)
        {
            var list = new List<string>();
            foreach (var menu in army.Menus)
                foreach (var b in menu.Buttons.Where(b => KeyFor(b.Label) == key))
                    list.Add($"{b.Name} · {menu.Name}");
            if (PlainGlobalKey(key) is { } global)
                list.Insert(0, $"Game key: {global.Name}");
            return list;
        }

        // Grid layout: each button gets the key of its position on the control bar (two rows of seven).
        // Buttons the game ships without a key (Sell, Exit) keep what they have, so no layout sells by accident.
        public void ApplyGridLayout()
        {
            const string rows = "QWERTYUASDFGHJZXCVBNM";
            var bySlot = Armies.SelectMany(a => a.Menus).SelectMany(m => m.Buttons).Where(b => DefaultKeyFor(b.Label) != '\0')
                .GroupBy(b => b.Label, StringComparer.OrdinalIgnoreCase);
            foreach (var group in bySlot)
            {
                int slot = group.GroupBy(b => b.Slot).OrderByDescending(g => g.Count()).First().Key;
                if (slot >= 1 && slot <= rows.Length)
                    SetKey(group.Key, rows[slot - 1]);
            }
        }

        public void ApplyClassicLayout()
        {
            foreach (string label in Armies.SelectMany(a => a.Menus).SelectMany(m => m.Buttons).Select(b => b.Label).Distinct(StringComparer.OrdinalIgnoreCase))
                ResetKey(label);
        }

        public int ButtonCount => Armies.SelectMany(a => a.Menus).SelectMany(m => m.Buttons).Select(b => b.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        private void LoadArmies()
        {
            Armies.Clear();
            var buttons = ParseBlocks(ReadMulti(@"Data\INI\CommandButton"), "CommandButton");
            var sets = ParseBlocks(ReadMulti(@"Data\INI\CommandSet"), "CommandSet");
            var objects = ParseObjects();

            foreach (var (side, armyName, faction, general, prefix) in ArmyDefs)
            {
                var menus = new List<HotkeyMenu>();

                // Several objects can share a name (upgraded or campaign copies); the first one that has a
                // menu with hotkeys wins, and a second name only appears when its buttons differ
                foreach (var obj in objects.Where(o => o.Side == side && o.CommandSet != null)
                                           .OrderBy(o => o.IsStructure ? 0 : 1)
                                           .ThenBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    var menu = BuildMenu(obj.DisplayName, obj.IsStructure ? MenuKind.Structure : MenuKind.Unit, obj.CommandSet!, sets, buttons);
                    if (menu == null)
                        continue;
                    var same = menus.FirstOrDefault(m => m.Name.Equals(menu.Name, StringComparison.OrdinalIgnoreCase));
                    if (same == null)
                        menus.Add(menu);
                    else if (menu.Buttons.Count > same.Buttons.Count)
                        menus[menus.IndexOf(same)] = menu;
                }

                foreach (int rank in new[] { 1, 3, 8 })
                {
                    string setName = (prefix != null ? prefix + "_" : "") + $"SCIENCE_{(faction == "USA" ? "AMERICA" : faction.ToUpperInvariant())}_CommandSetRank{rank}";
                    var menu = BuildMenu($"Rank {rank} powers", MenuKind.Powers, setName, sets, buttons);
                    if (menu != null)
                        menus.Add(menu);
                }

                Armies.Add(new HotkeyArmy(armyName, faction, general, menus));
            }
        }

        // The game loads "X.ini" and then every file in the folder "X\" (Generals Online's community patch keeps its data there)
        private string ReadMulti(string basePath)
        {
            var text = new StringBuilder(_files.ReadText(basePath + ".ini"));
            foreach (string path in _files.List(basePath + @"\").Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
                text.Append('\n').Append(_files.ReadText(path));
            return text.ToString();
        }

        private HotkeyMenu? BuildMenu(string name, MenuKind kind, string setName,
            Dictionary<string, Dictionary<string, string>> sets, Dictionary<string, Dictionary<string, string>> buttons)
        {
            if (!sets.TryGetValue(setName, out var set))
                return null;

            var list = new List<HotkeyButton>();
            var silent = new List<HotkeyButton>();
            foreach (var (slotText, command) in set)
            {
                if (!int.TryParse(slotText, out int slot) || !buttons.TryGetValue(command, out var button))
                    continue;
                button.TryGetValue("ButtonImage", out var image);
                button.TryGetValue("Command", out var action);
                button.TryGetValue("TextLabel", out var label);
                string? labelText = label != null ? _originalCsf.Get(label) : null;
                string text = label != null ? (labelText ?? label).Replace("&", "") : command;
                // A key needs text to put the '&' in; buttons without one stay silent
                if (label == null || string.IsNullOrWhiteSpace(labelText))
                {
                    silent.Add(new HotkeyButton(command, label ?? "", slot, text, image, action ?? ""));
                    continue;
                }
                list.Add(new HotkeyButton(command, label, slot, text, image, action ?? ""));
            }

            // Menus without a single key in the game (a defence with only Sell) stay out of the list
            return list.Any(b => CsfFile.HotkeyOf(_originalCsf.Get(b.Label)) != '\0')
                ? new HotkeyMenu(name, kind, setName, list.OrderBy(b => b.Slot).ToList(), silent)
                : null;
        }

        private sealed record GameObject(string Side, string DisplayName, string? CommandSet, bool IsStructure);

        private sealed class ParsedObject
        {
            public required string Name;
            public string? ReskinOf, Side, Display, CommandSet;
            public bool? Structure, Buildable, HasCost;
        }

        private List<GameObject> ParseObjects()
        {
            // A header is "Object Name" or "ObjectReskin Name BaseName" on a line without '='. The community patch
            // does not indent module lines, so "Object = X" inside a module must not start a new object.
            var header = new Regex(@"^\s*(Object|ObjectReskin)\s+([^\s=;]+)(?:\s+([^\s=;]+))?\s*(?:;.*)?$", RegexOptions.Compiled);
            var parsed = new Dictionary<string, ParsedObject>(StringComparer.OrdinalIgnoreCase);

            // The community patch keeps campaign and cinematic objects in a folder of their own
            foreach (string path in _files.List(@"Data\INI\Object\").Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                                                                                 && !p.Contains(@"\Campaign\", StringComparison.OrdinalIgnoreCase)))
            {
                ParsedObject? current = null;
                foreach (string raw in _files.ReadText(path).Split('\n'))
                {
                    var match = header.Match(raw);
                    if (match.Success)
                    {
                        current = new ParsedObject { Name = match.Groups[2].Value, ReskinOf = match.Groups[1].Value == "ObjectReskin" && match.Groups[3].Success ? match.Groups[3].Value : null };
                        parsed[current.Name] = current;
                        continue;
                    }
                    if (current == null)
                        continue;

                    string line = StripComment(raw);
                    int eq = line.IndexOf('=');
                    if (eq < 0)
                        continue;
                    string key = line[..eq].Trim();
                    string value = line[(eq + 1)..].Trim();

                    if (current.Side == null && key.Equals("Side", StringComparison.OrdinalIgnoreCase)) current.Side = value;
                    else if (current.Display == null && key.Equals("DisplayName", StringComparison.OrdinalIgnoreCase)) current.Display = value;
                    else if (current.CommandSet == null && key.Equals("CommandSet", StringComparison.OrdinalIgnoreCase)) current.CommandSet = value;
                    else if (current.Structure == null && key.Equals("KindOf", StringComparison.OrdinalIgnoreCase)) current.Structure = Regex.IsMatch(value, @"\bSTRUCTURE\b");
                    else if (current.Buildable == null && key.Equals("Buildable", StringComparison.OrdinalIgnoreCase)) current.Buildable = !value.StartsWith("No", StringComparison.OrdinalIgnoreCase);
                    else if (current.HasCost == null && key.Equals("BuildCost", StringComparison.OrdinalIgnoreCase) && float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out float cost)) current.HasCost = cost > 0;
                }
            }

            // A reskin starts as a copy of its base object; only the fields it sets itself differ
            ParsedObject Resolve(ParsedObject o, int depth = 0)
            {
                if (o.ReskinOf == null || depth > 8 || !parsed.TryGetValue(o.ReskinOf, out var source))
                    return o;
                var b = Resolve(source, depth + 1);
                return new ParsedObject
                {
                    Name = o.Name,
                    Side = o.Side ?? b.Side, Display = o.Display ?? b.Display, CommandSet = o.CommandSet ?? b.CommandSet,
                    Structure = o.Structure ?? b.Structure, Buildable = o.Buildable ?? b.Buildable, HasCost = o.HasCost ?? b.HasCost,
                };
            }

            var result = new List<GameObject>();
            foreach (var raw in parsed.Values)
            {
                var o = Resolve(raw);
                bool cinematic = o.Name.StartsWith("CINE_", StringComparison.OrdinalIgnoreCase) || o.Name.StartsWith("MISSION_", StringComparison.OrdinalIgnoreCase);
                // Only things a player can build appear (campaign-only objects are left out)
                if (!cinematic && o.Side != null && o.CommandSet != null && o.Buildable != false && o.HasCost == true)
                    result.Add(new GameObject(o.Side, o.Display != null ? (_originalCsf.Get(o.Display) ?? o.Name) : o.Name, o.CommandSet, o.Structure == true));
            }
            return result;
        }

        // ── Global keys (CommandMap.ini) ──

        private sealed record MapBlock(string Command, int Line, Dictionary<string, string> Values);

        private static List<MapBlock> ParseCommandMap(string[] lines)
        {
            var blocks = new List<MapBlock>();
            for (int i = 0; i < lines.Length; i++)
            {
                var match = Regex.Match(lines[i], @"^\s*CommandMap\s+(\S+)");
                if (!match.Success)
                    continue;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string line = StripComment(lines[j]).Trim();
                    if (line.Equals("End", StringComparison.OrdinalIgnoreCase))
                        break;
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
                blocks.Add(new MapBlock(match.Groups[1].Value, i, values));
            }
            return blocks;
        }

        private void LoadGameKeys()
        {
            GameKeys.Clear();
            _commandMapLines = ReadLines(CommandMapPath);
            var current = ParseCommandMap(_commandMapLines);
            var original = ParseCommandMap(Encoding.Latin1.GetString(_files.ReadFromArchive(CommandMapPath) ?? Array.Empty<byte>()).Replace("\r\n", "\n").Split('\n'))
                .GroupBy(b => b.Command, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            int order = 0;
            foreach (var block in current)
            {
                string command = block.Command;
                order++;
                if (IsEndCommand(command) || command.StartsWith("DEMO_", StringComparison.OrdinalIgnoreCase) || HiddenCommands.Contains(command))
                    continue;
                if (!block.Values.TryGetValue("Key", out var key))
                    continue;

                string fileKey = key.ToUpperInvariant();
                string mods = block.Values.GetValueOrDefault("Modifiers", "NONE").ToUpperInvariant();
                var origin = original.GetValueOrDefault(command) ?? block;
                string originalKey = origin.Values.GetValueOrDefault("Key", key).ToUpperInvariant();
                string originalMods = origin.Values.GetValueOrDefault("Modifiers", "NONE").ToUpperInvariant();
                bool team = TeamCommand.IsMatch(command);
                string name = CommandNames.TryGetValue(command, out var known) ? Loc.T(known)
                    : block.Values.TryGetValue("DisplayName", out var display) && _originalCsf.Get(display) is { } text ? text.Replace("&", "")
                    : Readable(command);

                GameKeys.Add(new GameKey
                {
                    Command = command,
                    Name = name,
                    Category = CategoryOf(command, block.Values.GetValueOrDefault("Category", "")),
                    Block = block.Line,
                    OriginalKey = originalKey,
                    OriginalModifiers = originalMods,
                    Transition = block.Values.GetValueOrDefault("Transition", "DOWN").ToUpperInvariant(),
                    UseableIn = block.Values.GetValueOrDefault("UseableIn", "GAME").ToUpperInvariant(),
                    IsTeam = team,
                    Order = order,
                    FileKey = fileKey,
                    FileModifiers = mods,
                    // Team keys always go back to the game's defaults; one changed in the file is a pending repair
                    Key = team ? originalKey : fileKey,
                    Modifiers = team ? originalMods : mods,
                });
            }

            // Built-in keys: the game's own unless our file in Data\<language>\CommandMap\ sets another one
            var defined = current.Select(b => b.Command).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = ParseCommandMap(ReadLines(BuiltInMapPath))
                .GroupBy(b => b.Command, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var (command, name, key, mods, usable, category) in BuiltIns)
            {
                if (defined.Contains(command))
                    continue;
                bool isFixed = FixedBuiltIns.Contains(command);
                string fileKey = key, fileMods = mods;
                // KEY_NONE leaves the record empty, and the game then adds its default after all
                if (!isFixed && changed.TryGetValue(command, out var block) && block.Values.TryGetValue("Key", out var setKey)
                    && !setKey.Equals("KEY_NONE", StringComparison.OrdinalIgnoreCase))
                {
                    fileKey = setKey.ToUpperInvariant();
                    fileMods = block.Values.GetValueOrDefault("Modifiers", "NONE").ToUpperInvariant();
                }
                GameKeys.Add(new GameKey
                {
                    Command = command, Name = Loc.T(name), Category = "BUILT-IN", Block = -1, IsBuiltIn = true, IsFixed = isFixed,
                    OriginalKey = key, OriginalModifiers = mods, Transition = "DOWN", UseableIn = usable, Order = 10000 + order++,
                    FileKey = fileKey, FileModifiers = fileMods, Key = fileKey, Modifiers = fileMods,
                    BuiltInCategory = category,
                });
            }
        }

        private string[] ReadLines(string relativePath) => _files.ReadText(relativePath).Replace("\r\n", "\n").Split('\n');

        // END_x (and ALT_END_x) blocks follow their BEGIN_x on key up; the editor never lists them
        private static bool IsEndCommand(string command) =>
            command.StartsWith("END_", StringComparison.OrdinalIgnoreCase) || command.StartsWith("ALT_END_", StringComparison.OrdinalIgnoreCase);

        private static string? BeginOf(string endCommand) =>
            endCommand.StartsWith("END_", StringComparison.OrdinalIgnoreCase) ? "BEGIN_" + endCommand[4..]
            : endCommand.StartsWith("ALT_END_", StringComparison.OrdinalIgnoreCase) ? "ALT_BEGIN_" + endCommand[8..]
            : null;

        private static string? EndOf(string beginCommand) =>
            beginCommand.StartsWith("BEGIN_", StringComparison.OrdinalIgnoreCase) ? "END_" + beginCommand[6..]
            : beginCommand.StartsWith("ALT_BEGIN_", StringComparison.OrdinalIgnoreCase) ? "ALT_END_" + beginCommand[10..]
            : null;

        private static string CategoryOf(string command, string category)
        {
            if (command.Contains("CAMERA", StringComparison.OrdinalIgnoreCase)) return "CAMERA";
            if (command.StartsWith("CHAT", StringComparison.OrdinalIgnoreCase) || command.Contains("BEACON", StringComparison.OrdinalIgnoreCase) || command == "DIPLOMACY") return "CHAT";
            return category.Length > 0 ? category.ToUpperInvariant() : "INTERFACE";
        }

        public static string TeamGroupOf(GameKey key)
        {
            var m = Regex.Match(key.Command, @"^(CREATE|SELECT|ADD|VIEW)_TEAM", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        public List<KeyIssue> GameKeyIssues(GameKey gameKey)
        {
            var issues = new List<KeyIssue>();
            if (gameKey.Key is "KEY_NONE" or "")
                return issues;

            foreach (var other in GameKeys.Where(k => k != gameKey && k.Key == gameKey.Key && k.Modifiers == gameKey.Modifiers && k.Transition == gameKey.Transition))
            {
                if (!UsableTogether(gameKey.UseableIn, other.UseableIn))
                    continue;
                string shortcut = Loc.Ltr(gameKey.Shortcut);
                issues.Add(new KeyIssue(IssueLevel.Error,
                    other.IsTeam ? Loc.T("{0} is the team key {1}, and team keys cannot change. Pick another key.", shortcut, other.Name)
                    : other.IsBuiltIn ? Loc.T("{0} is built into the game for {1}; only one of them will work.", shortcut, other.Name)
                    : Loc.T("{0} is also set for {1}; only one of them will work.", shortcut, other.Name)));
            }

            if (gameKey.Modifiers == "NONE" && gameKey.Key.Length == 5 && gameKey.Key.StartsWith("KEY_"))
            {
                char letter = gameKey.Key[4];
                var buttons = Armies.SelectMany(a => a.Menus).SelectMany(m => m.Buttons)
                    .Where(b => KeyFor(b.Label) == letter && !b.Action.Equals(gameKey.Command, StringComparison.OrdinalIgnoreCase))
                    .Select(b => b.Name).Distinct().Take(3).ToList();
                if (buttons.Count > 0)
                    issues.Add(new KeyIssue(IssueLevel.Warn, Loc.T("{0} is also a button key ({1}…). Pressing it does both.", letter, string.Join(Loc.T(", "), buttons))));
            }
            return issues;
        }

        private static bool UsableTogether(string a, string b) =>
            a.Contains("EVERYWHERE") || b.Contains("EVERYWHERE") || a == b
            || (a.Contains("GAME") && b.Contains("GAME")) || (a.Contains("OBSERVER") && b.Contains("OBSERVER"));

        // Why a key cannot go on this command, or null when it can. CommandMap.ini is read by the original game too,
        // which stops at startup on a key name it does not know; the built-in keys' own file is read by Generals Online only.
        public static string? CannotUse(GameKey gameKey, string key)
        {
            if (gameKey.IsTeam)
                return Loc.T("Team keys always use the game's defaults.");
            if (gameKey.IsFixed)
                return Loc.T("The game adds this key by itself and cannot read a new one for it.");
            if (!gameKey.IsBuiltIn && NewKeyNames.Contains(key))
                return Loc.T("{0} only works in Generals Online; the original game would not start with it in CommandMap.ini.", Loc.Ltr(Describe(key, "NONE")));
            return null;
        }

        public void SetGameKey(GameKey gameKey, string key, string modifiers)
        {
            if (CannotUse(gameKey, key) != null)
                return;
            gameKey.Key = key;
            gameKey.Modifiers = modifiers;
        }

        public void ResetGameKey(GameKey gameKey)
        {
            gameKey.Key = gameKey.OriginalKey;
            gameKey.Modifiers = gameKey.OriginalModifiers;
        }

        // Every game key the editor can change goes back to the game's default (the Classic layout)
        public void ResetGameKeys()
        {
            foreach (var k in GameKeys.Where(k => k.CanChange))
                ResetGameKey(k);
        }

        public static string Describe(string key, string modifiers)
        {
            string name = key.StartsWith("KEY_") ? key[4..] : key;
            name = name switch
            {
                "ESC" => "Esc", "SPACE" => "Space", "TAB" => "Tab", "ENTER" => "Enter", "BACKSPACE" => "Backspace",
                "DEL" => "Delete", "INS" => "Insert", "PGUP" => "Page Up", "PGDN" => "Page Down", "HOME" => "Home", "END" => "End",
                "UP" => "Up", "DOWN" => "Down", "LEFT" => "Left", "RIGHT" => "Right", "MINUS" => "-", "EQUAL" => "=",
                "LBRACKET" => "[", "RBRACKET" => "]", "SEMICOLON" => ";", "APOSTROPHE" => "'", "TICK" => "`",
                "BACKSLASH" => "\\", "COMMA" => ",", "PERIOD" => ".", "SLASH" => "/", "KPPLUS" => "Num +", "KPMINUS" => "Num -",
                "NONE" => "—",
                _ when name.StartsWith("KP") => "Num " + name[2..],
                _ => name,
            };
            string mods = modifiers switch
            {
                "CTRL" => "Ctrl + ", "ALT" => "Alt + ", "SHIFT" => "Shift + ", "SHIFT_CTRL" => "Ctrl + Shift + ",
                "SHIFT_ALT" => "Alt + Shift + ", "CTRL_ALT" => "Ctrl + Alt + ", "SHIFT_ALT_CTRL" => "Ctrl + Alt + Shift + ",
                _ => "",
            };
            return mods + name;
        }

        // Maps a WPF key to the game's KEY_ name; null when the game has no such key
        public static string? GameKeyName(System.Windows.Input.Key key)
        {
            {
                if (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z)
                    return "KEY_" + key;
                if (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9)
                    return "KEY_" + (key - System.Windows.Input.Key.D0);
                if (key >= System.Windows.Input.Key.F1 && key <= System.Windows.Input.Key.F12)
                    return "KEY_" + key;
                if (key >= System.Windows.Input.Key.NumPad0 && key <= System.Windows.Input.Key.NumPad9)
                    return "KEY_KP" + (key - System.Windows.Input.Key.NumPad0);
                return key switch
                {
                    System.Windows.Input.Key.Space => "KEY_SPACE",
                    System.Windows.Input.Key.Tab => "KEY_TAB",
                    System.Windows.Input.Key.Enter => "KEY_ENTER",
                    System.Windows.Input.Key.Back => "KEY_BACKSPACE",
                    System.Windows.Input.Key.Delete => "KEY_DEL",
                    System.Windows.Input.Key.Insert => "KEY_INS",
                    System.Windows.Input.Key.Home => "KEY_HOME",
                    System.Windows.Input.Key.End => "KEY_END",
                    System.Windows.Input.Key.PageUp => "KEY_PGUP",
                    System.Windows.Input.Key.PageDown => "KEY_PGDN",
                    System.Windows.Input.Key.Up => "KEY_UP",
                    System.Windows.Input.Key.Down => "KEY_DOWN",
                    System.Windows.Input.Key.Left => "KEY_LEFT",
                    System.Windows.Input.Key.Right => "KEY_RIGHT",
                    System.Windows.Input.Key.OemMinus => "KEY_MINUS",
                    System.Windows.Input.Key.OemPlus => "KEY_EQUAL",
                    System.Windows.Input.Key.OemOpenBrackets => "KEY_LBRACKET",
                    System.Windows.Input.Key.OemCloseBrackets => "KEY_RBRACKET",
                    System.Windows.Input.Key.OemSemicolon => "KEY_SEMICOLON",
                    System.Windows.Input.Key.OemQuotes => "KEY_APOSTROPHE",
                    System.Windows.Input.Key.OemTilde => "KEY_TICK",
                    System.Windows.Input.Key.OemBackslash or System.Windows.Input.Key.Oem5 => "KEY_BACKSLASH",
                    System.Windows.Input.Key.OemComma => "KEY_COMMA",
                    System.Windows.Input.Key.OemPeriod => "KEY_PERIOD",
                    System.Windows.Input.Key.OemQuestion => "KEY_SLASH",
                    System.Windows.Input.Key.Add => "KEY_KPPLUS",
                    System.Windows.Input.Key.Subtract => "KEY_KPMINUS",
                    _ => null,
                };
            }
        }

        public static string ModifiersName(bool ctrl, bool alt, bool shift) => (ctrl, alt, shift) switch
        {
            (false, false, false) => "NONE",
            (true, false, false) => "CTRL",
            (false, true, false) => "ALT",
            (false, false, true) => "SHIFT",
            (true, false, true) => "SHIFT_CTRL",
            (false, true, true) => "SHIFT_ALT",
            (true, true, false) => "CTRL_ALT",
            _ => "SHIFT_ALT_CTRL",
        };

        // ── Saving ──

        // The files Save would write right now (relative to the game folder)
        public List<string> PendingFiles()
        {
            var files = new List<string>();
            if (_pendingKeys.Any(p => p.Value != SavedKeyFor(p.Key)))
                files.Add(CsfPath);
            if (GameKeys.Any(k => k.Pending && !k.IsBuiltIn))
                files.Add(CommandMapPath);
            if (GameKeys.Any(k => k.Pending && k.IsBuiltIn))
                files.Add(BuiltInMapPath);
            return files;
        }

        public string Save()
        {
            var parts = new List<string>();
            int buttons = _pendingKeys.Count(p => p.Value != SavedKeyFor(p.Key));
            if (buttons > 0)
            {
                BackupService.WriteFile("Saved hotkeys", _files.LoosePath(CsfPath), BuildCsf(), $"{CsfPath} · {buttons} buttons");
                parts.Add("generals.csf");
            }

            // Changed team keys are written back to their defaults here too
            int mapped = GameKeys.Count(k => k.Pending && !k.IsBuiltIn);
            if (mapped > 0)
            {
                BackupService.WriteFile("Saved game keys", _files.LoosePath(CommandMapPath), Encoding.Latin1.GetBytes(BuildCommandMap()), $"{CommandMapPath} · {mapped} keys");
                parts.Add("CommandMap.ini");
            }

            int builtIn = GameKeys.Count(k => k.Pending && k.IsBuiltIn);
            if (builtIn > 0)
            {
                BackupService.WriteFile("Saved built-in keys", _files.LoosePath(BuiltInMapPath), Encoding.Latin1.GetBytes(BuildBuiltInMap()), $"{BuiltInMapPath} · {builtIn} keys");
                parts.Add(@"CommandMap\CommandCenter.ini");
            }

            Reload();
            return parts.Count <= 1 ? string.Join("", parts) : string.Join(", ", parts.SkipLast(1)) + " and " + parts[^1];
        }

        // Removes our loose files' changes: the game falls back to its own keys
        public void ResetAll()
        {
            BackupService.RestoreOriginal(_files.LoosePath(CsfPath));
            BackupService.RestoreOriginal(_files.LoosePath(CommandMapPath));
            BackupService.RestoreOriginal(_files.LoosePath(BuiltInMapPath));
            Reload();
        }

        internal byte[] BuildCsf()
        {
            foreach (var (label, key) in _pendingKeys)
                _csf.Set(label, LabelText(_originalCsf.Get(label) ?? "", key));
            return _csf.ToBytes();
        }

        // Rewrites the Key and Modifiers lines of each changed block (team keys go back to their defaults);
        // BEGIN_x changes also apply to END_x.
        internal string BuildCommandMap()
        {
            var lines = _commandMapLines.ToList();
            var byBlock = GameKeys.Where(k => k.Pending && !k.IsBuiltIn).ToDictionary(k => k.Block);
            var byCommand = new Dictionary<string, GameKey>(StringComparer.OrdinalIgnoreCase);
            foreach (var gameKey in GameKeys.Where(k => k.Pending && !k.IsBuiltIn))
                byCommand.TryAdd(gameKey.Command, gameKey);

            // Walk from the bottom so inserted lines do not shift blocks still to be visited
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var match = Regex.Match(lines[i], @"^\s*CommandMap\s+(\S+)");
                if (!match.Success)
                    continue;

                string command = match.Groups[1].Value;
                GameKey? change = null;
                if (byBlock.TryGetValue(i, out var direct))
                    change = direct;
                else if (BeginOf(command) is { } begin)
                    byCommand.TryGetValue(begin, out change);
                if (change == null)
                    continue;

                int keyLine = -1, modLine = -1, end = i + 1;
                for (; end < lines.Count && !StripComment(lines[end]).Trim().Equals("End", StringComparison.OrdinalIgnoreCase); end++)
                {
                    string t = StripComment(lines[end]).Trim();
                    if (Regex.IsMatch(t, @"^Key\s*=", RegexOptions.IgnoreCase)) keyLine = end;
                    else if (Regex.IsMatch(t, @"^Modifiers\s*=", RegexOptions.IgnoreCase)) modLine = end;
                }
                if (keyLine >= 0) lines[keyLine] = "  Key = " + change.Key;
                if (modLine >= 0) lines[modLine] = "  Modifiers = " + change.Modifiers;
                else if (keyLine >= 0) lines.Insert(keyLine + 1, "  Modifiers = " + change.Modifiers);
            }

            return string.Join("\r\n", lines);
        }

        // A block for every built-in key that differs from the game's own; a BEGIN_x key also gets its END_x on key up.
        // With no block left the file only holds the comment, and the game adds all its own keys again.
        internal string BuildBuiltInMap()
        {
            var text = new StringBuilder();
            text.Append("; Written by Command Center: keys that Generals Online adds by itself, moved to other keys.\r\n");
            text.Append("; Only Generals Online reads this folder. Without this file the game uses its own keys.\r\n");
            foreach (var k in GameKeys.Where(k => k.IsBuiltIn && k.CanChange && k.Changed))
            {
                // The parser knows SHELL, GAME and OBSERVER; the game's own EVERYWHERE is all three
                string usable = k.UseableIn == "EVERYWHERE" ? "SHELL GAME OBSERVER" : k.UseableIn;
                AppendBlock(text, k.Command, k.Key, "DOWN", k.Modifiers, usable, k.BuiltInCategory);
                if (EndOf(k.Command) is { } end)
                    AppendBlock(text, end, k.Key, "UP", k.Modifiers, usable, k.BuiltInCategory);
            }
            return text.ToString();
        }

        private static void AppendBlock(StringBuilder text, string command, string key, string transition, string modifiers, string usable, string category) =>
            text.Append("\r\nCommandMap ").Append(command).Append("\r\n")
                .Append("  Key = ").Append(key).Append("\r\n")
                .Append("  Transition = ").Append(transition).Append("\r\n")
                .Append("  Modifiers = ").Append(modifiers).Append("\r\n")
                .Append("  UseableIn = ").Append(usable).Append("\r\n")
                .Append("  Category = ").Append(category).Append("\r\n")
                .Append("End\r\n");

        // "SAVE_VIEW5" -> "Save view 5", used when the game has no display text for a command
        private static string Readable(string command)
        {
            string spaced = Regex.Replace(command.Replace('_', ' '), @"(\D)(\d)", "$1 $2").ToLowerInvariant();
            return char.ToUpperInvariant(spaced[0]) + spaced[1..];
        }

        // ── INI helpers ──

        private static string StripComment(string line)
        {
            int c = line.IndexOf(';');
            return c >= 0 ? line[..c] : line;
        }

        private static Dictionary<string, Dictionary<string, string>> ParseBlocks(string text, string kind)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? current = null;
            var header = new Regex("^" + kind + @"\s+(\S+)", RegexOptions.IgnoreCase);

            foreach (string raw in text.Split('\n'))
            {
                string line = StripComment(raw).Trim();
                if (line.Length == 0)
                    continue;
                if (current == null)
                {
                    var match = header.Match(line);
                    if (match.Success)
                    {
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        result[match.Groups[1].Value] = current;
                    }
                    continue;
                }
                if (line.Equals("End", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0)
                    current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }

            return result;
        }
    }
}
