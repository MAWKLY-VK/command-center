using System.IO;
using System.Text.Json;

namespace CommandCenter.Services
{
    // Command Center's own preferences, kept apart from the game's and Generals Online's files.
    public sealed class AppSettings
    {
        public string? LibraryFolder { get; set; }

        // Where the Maps page's Library tab reads from: a web address or a folder; empty means the online library
        public string? MapLibrary { get; set; }

        // Hotkey letters drawn on the unit and building pictures in the game (IconLettersService)
        public bool IconLetters { get; set; }

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
