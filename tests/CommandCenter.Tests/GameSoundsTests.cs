using System.Text;

namespace CommandCenter.Tests
{
    // Interface sounds (ticket 14): the click and the launch sound are the files the game's own audio settings name, read
    // from its archives with a size limit and turned into plain PCM at the volume the game gives them. A missing, damaged
    // or oversized sound is skipped and written to the error log.
    public sealed class GameSoundsTests : IDisposable
    {
        private readonly TempGame _game = new();

        // A WAV file: the RIFF header, the fmt chunk (with extra bytes for compressed formats) and the data chunk
        private static byte[] Wave(ushort format, ushort channels, int rate, ushort bits, ushort blockAlign, byte[] data, byte[]? extra = null)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            int fmtSize = 16 + (extra != null ? 2 + extra.Length : 0);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(4 + 8 + fmtSize + 8 + data.Length);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(fmtSize);
            w.Write(format);
            w.Write(channels);
            w.Write(rate);
            w.Write(rate * blockAlign);
            w.Write(blockAlign);
            w.Write(bits);
            if (extra != null)
            {
                w.Write((ushort)extra.Length);
                w.Write(extra);
            }
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(data.Length);
            w.Write(data);
            w.Flush();
            return stream.ToArray();
        }

        private static byte[] Pcm16(params short[] samples) =>
            Wave(1, 1, 22050, 16, 2, samples.SelectMany(s => BitConverter.GetBytes(s)).ToArray());

        // The 16-bit samples of a PCM WAV made by WaveFiles
        private static short[] Samples(byte[] wave)
        {
            int data = Encoding.ASCII.GetString(wave).IndexOf("data", StringComparison.Ordinal) + 8;
            return Enumerable.Range(0, (wave.Length - data) / 2).Select(i => BitConverter.ToInt16(wave, data + i * 2)).ToArray();
        }

        private void Archive(string relative, params (string Name, byte[] Data)[] files) => BigArchives.Write(Path.Combine(_game.Game, relative), files);

        private static (string, byte[]) Text(string name, string text) => (name, Encoding.ASCII.GetBytes(text));

        private GameSounds.Set Read() => GameSounds.Read(new GameFiles(_game.Game, communityPatch: false));

        // ── Decoding ──

        [Fact]
        public void Ima_adpcm_is_decoded_to_pcm()
        {
            // One mono block: the first sample 0 at step index 0, then four nibbles of 7. By the IMA tables each 7 adds
            // the whole step plus a half, a quarter and an eighth of it: 0+7+3+1 = 11, then step 16 gives 30 more, step 34
            // gives 63 more and step 73 gives 136 more.
            byte[] wave = Wave(0x11, 1, 22050, 4, 6, [0, 0, 0, 0, 0x77, 0x77], extra: [5, 0]);
            Assert.Equal([0, 11, 41, 104, 240], Samples(WaveFiles.ToPcm16(wave, volume: 1)!));

            // The sign bit takes the same steps down
            byte[] down = Wave(0x11, 1, 22050, 4, 6, [0, 0, 0, 0, 0xFF, 0xFF], extra: [5, 0]);
            Assert.Equal([0, -11, -41, -104, -240], Samples(WaveFiles.ToPcm16(down, volume: 1)!));
        }

        [Fact]
        public void Pcm_plays_at_the_volume_the_game_gives_it()
        {
            Assert.Equal([300, -600, 9830], Samples(WaveFiles.ToPcm16(Pcm16(1000, -2000, 32767), volume: 0.3)!));
        }

        [Fact]
        public void Something_that_is_not_a_sound_Command_Center_can_play_gives_nothing()
        {
            Assert.Null(WaveFiles.ToPcm16(Encoding.ASCII.GetBytes("not a wave file at all, just text"), volume: 1));
            Assert.Null(WaveFiles.ToPcm16(Wave(0x55, 2, 44100, 0, 1, [1, 2, 3, 4]), volume: 1));
            Assert.Null(WaveFiles.ToPcm16(Wave(1, 1, 22050, 16, 2, [1, 2, 3, 4])[..30], volume: 1));
        }

        // ── Reading the game's sounds ──

        [Fact]
        public void The_click_and_the_launch_are_the_sounds_the_games_audio_settings_name()
        {
            Archive("INIZH.big",
                Text(@"Data\INI\AudioSettings.ini", "AudioRoot = Data\\Audio ; relative\nSoundsFolder = Sounds\nSoundsExtension = wav\n"),
                Text(@"Data\INI\MiscAudio.ini", "MiscAudio\n  GUIClickSound = MyClick ; played when user presses a button in UI\nEnd\n"),
                Text(@"Data\INI\SoundEffects.ini", "AudioEvent MyClick\n  Sounds = clicka clickb\n  Volume = 30\nEnd\n\nAudioEvent GUILogoSelect\n  Sounds = logosel\n  Volume = 40%\nEnd\n"));
            Archive(@"ZH_Generals\Audio.big", (@"Data\Audio\Sounds\clicka.wav", Pcm16(1000)), (@"Data\Audio\Sounds\logosel.wav", Pcm16(2000)));

            var sounds = Read();
            Assert.Equal([300], Samples(sounds.Click!));
            Assert.Equal([800], Samples(sounds.Launch!));
        }

        [Fact]
        public void Without_the_audio_settings_the_games_own_defaults_name_the_click()
        {
            Archive("INIZH.big", Text(@"Data\INI\SoundEffects.ini", "AudioEvent GUIClick\n  Sounds = umenucla\nEnd\n"));
            Archive(@"ZH_Generals\Audio.big", (@"Data\Audio\Sounds\umenucla.wav", Pcm16(1000)));

            var sounds = Read();
            Assert.Equal([1000], Samples(sounds.Click!));
            Assert.Null(sounds.Launch);
        }

        [Fact]
        public void A_missing_damaged_or_oversized_sound_is_skipped_and_logged()
        {
            Archive("INIZH.big", Text(@"Data\INI\SoundEffects.ini", "AudioEvent GUIClick\n  Sounds = broken\nEnd\nAudioEvent GUILogoSelect\n  Sounds = huge\nEnd\n"));
            Archive(@"ZH_Generals\Audio.big",
                Text(@"Data\Audio\Sounds\broken.wav", "this is not a wave file"),
                (@"Data\Audio\Sounds\huge.wav", Pcm16(new short[GameSounds.SizeLimit / 2 + 8])));

            var sounds = Read();
            Assert.Null(sounds.Click);
            Assert.Null(sounds.Launch);
            string log = File.ReadAllText(ErrorLog.FilePath);
            Assert.Contains("broken.wav", log);
            Assert.Contains("huge.wav", log);

            Archive("INIZH.big", Text(@"Data\INI\SoundEffects.ini", "AudioEvent GUIClick\n  Sounds = gone\nEnd\n"));
            Assert.Null(Read().Click);
        }

        public void Dispose() => _game.Dispose();
    }
}
