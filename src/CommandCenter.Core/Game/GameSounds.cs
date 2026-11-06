using System.IO;

namespace CommandCenter.Core.Game
{
    // The game's own interface sounds for Command Center (ticket 14): the click the game plays when a button is pressed
    // (MiscAudio.ini's GUIClickSound) and its main menu's select sound for PLAY (the GUILogoSelect event). The files are
    // the ones the game's audio settings name (AudioSettings.ini's folders, SoundEffects.ini's events), read the game's
    // way with a size limit and turned into plain PCM at the event's volume. A sound that is missing, too large or not a
    // WAV Command Center can play is skipped and written to the error log.
    public static class GameSounds
    {
        // The interface sounds on Home and the buttons; null where the game has none
        public sealed record Set(byte[]? Click, byte[]? Launch);

        // An interface sound is short: the largest the game uses is about 160 KB
        public const int SizeLimit = 4 * 1024 * 1024;

        public const string LaunchEvent = "GUILogoSelect";

        private sealed record AudioEvent(string Sound, double Volume);

        public static Set Read(GameFiles files)
        {
            var settings = Values(files.ReadText(@"Data\INI\AudioSettings.ini"));
            string root = settings.GetValueOrDefault("AudioRoot", @"Data\Audio");
            string folder = settings.GetValueOrDefault("SoundsFolder", "Sounds");
            string extension = settings.GetValueOrDefault("SoundsExtension", "wav");
            string click = Values(files.ReadText(@"Data\INI\MiscAudio.ini")).GetValueOrDefault("GUIClickSound", "GUIClick");
            var events = Events(files.ReadText(@"Data\INI\SoundEffects.ini"));
            return new Set(Sound(click), Sound(LaunchEvent));

            byte[]? Sound(string name)
            {
                if (!events.TryGetValue(name, out var audio))
                    return null;
                string path = Path.Combine(root, folder, audio.Sound + "." + extension);
                // The names come from the game's files: one that points outside the game's own data is not followed
                if (Path.IsPathRooted(path) || path.Contains(':') || path.Split('\\', '/').Contains(".."))
                {
                    ErrorLog.Skipped(path, "The sound's name points outside the game's data.");
                    return null;
                }
                if (!files.Exists(path))
                {
                    ErrorLog.Skipped(path, "The game names this sound, but it is not in its files.");
                    return null;
                }
                if (files.Read(path, SizeLimit) is not { } wave)
                    return null;
                var pcm = WaveFiles.ToPcm16(wave, audio.Volume);
                if (pcm == null)
                    ErrorLog.Skipped(path, "It is not a WAV sound Command Center can play.");
                return pcm;
            }
        }

        // "Key = Value ; comment" lines, the way the game's INI files set single values
        private static Dictionary<string, string> Values(string ini)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in ini.Split('\n'))
            {
                string line = raw.Split(';')[0].Trim();
                int equals = line.IndexOf('=');
                if (equals > 0)
                    values.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }
            return values;
        }

        // "AudioEvent Name" blocks up to "End": the first of their Sounds, and their Volume (a percentage)
        private static Dictionary<string, AudioEvent> Events(string ini)
        {
            var events = new Dictionary<string, AudioEvent>(StringComparer.OrdinalIgnoreCase);
            string? name = null;
            string? sound = null;
            double volume = 1;
            foreach (string raw in ini.Split('\n'))
            {
                string line = raw.Split(';')[0].Trim();
                var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                    continue;
                if (words[0].Equals("AudioEvent", StringComparison.OrdinalIgnoreCase) && words.Length > 1)
                {
                    (name, sound, volume) = (words[1], null, 1);
                    continue;
                }
                if (name == null)
                    continue;
                if (words[0].Equals("End", StringComparison.OrdinalIgnoreCase))
                {
                    if (sound != null)
                        events.TryAdd(name, new AudioEvent(sound, volume));
                    name = null;
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                if (key.Equals("Sounds", StringComparison.OrdinalIgnoreCase))
                    sound = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                else if (key.Equals("Volume", StringComparison.OrdinalIgnoreCase)
                         && double.TryParse(value.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent))
                    volume = Math.Clamp(percent / 100, 0, 1);
            }
            return events;
        }
    }
}
