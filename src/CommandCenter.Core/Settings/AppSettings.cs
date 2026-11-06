using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommandCenter.Core.Settings
{
    // Command Center's own preferences, kept apart from the game's and Generals Online's files.
    public sealed class AppSettings
    {
        // The game folder the player chose when it was not found by itself
        public string? GameFolder { get; set; }

        // 0.4.0's interface language, "en" or "ar". The one-time step of the English-only version reads it for its
        // notice and then clears it (ArabicGameTextRemoval.RunOnce).
        public string? Language { get; set; }

        // True once the one-time step of the English-only version has run (ArabicGameTextRemoval.RunOnce)
        public bool EnglishOnlyStepDone { get; set; }

        // Hotkey letters drawn on the unit and building pictures in the game (IconLettersService)
        public bool IconLetters { get; set; }

        // Home's picture: Changing (the next one at each start) or the one Settings pins (HomeArt)
        [JsonIgnore]
        public HomeImage HomeImage
        {
            get => Named(HomeImageName) ?? HomeImage.Changing;
            set => HomeImageName = value.ToString();
        }

        // The picture Home showed at the last start, which the next start follows; none before the first
        [JsonIgnore]
        public HomeImage? LastHomeImage
        {
            get => Named(LastHomeImageName);
            set => LastHomeImageName = value?.ToString();
        }

        // Both are stored by name, so a name this version does not know reads as unset instead of losing every setting
        public string? HomeImageName { get; set; }
        public string? LastHomeImageName { get; set; }

        private static HomeImage? Named(string? name) =>
            name != null && Enum.GetNames<HomeImage>().Contains(name) ? Enum.Parse<HomeImage>(name) : null;

        private static string FilePath => Path.Combine(GamePaths.AppData, "settings.json");
        private static AppSettings? _current;

        public static AppSettings Current
        {
            get
            {
                if (_current != null)
                    return _current;
                try
                {
                    _current = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) : null;
                }
                catch { }
                return _current ??= new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(GamePaths.AppData);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
