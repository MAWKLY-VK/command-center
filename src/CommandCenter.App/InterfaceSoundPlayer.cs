using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CommandCenter
{
    // The game's interface sounds in Command Center (ticket 14): its click on every button and its menu's select sound
    // when PLAY starts the game, only while Appearance's setting is on (off by default). The sounds are read from the
    // player's game once, off the UI thread: at startup while the setting is on, else the first time one is wanted, and
    // again after the game folder changes.
    internal sealed class InterfaceSoundPlayer(Func<bool> enabled, Func<GameSounds.Set> read, Action<byte[]> play)
    {
        private static readonly GameSounds.Set None = new(null, null);
        private Task<GameSounds.Set>? _sounds;

        public Task PlayClickAsync() => PlayAsync(sounds => sounds.Click);
        public Task PlayLaunchAsync() => PlayAsync(sounds => sounds.Launch);

        // At startup: read ahead while the setting is on, so that the first click is not late
        public Task PrepareAsync() => enabled() ? Sounds() : Task.CompletedTask;

        // Another game folder: its sounds are read the next time one is wanted
        public void Forget() => _sounds = null;

        private async Task PlayAsync(Func<GameSounds.Set, byte[]?> pick)
        {
            if (!enabled())
                return;
            try
            {
                if (pick(await Sounds()) is { } wave)
                    play(wave);
            }
            catch (Exception ex)
            {
                // A sound never stops what the player is doing
                ErrorLog.Write(ex);
            }
        }

        // A read that fails is written to the error log once and leaves no sounds until the game folder changes
        private Task<GameSounds.Set> Sounds() => _sounds ??= Task.Run(() =>
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                return None;
            }
        });
    }

    // Windows' player for short sounds (winmm's PlaySound), one at a time: a new sound stops the last. It reads the sound
    // from memory while the program goes on, so each sound is copied once into memory the runtime never moves, and the
    // one playing stays referenced until the next one replaces it.
    internal static class Speaker
    {
        private const uint Async = 0x0001, NoDefault = 0x0002, Memory = 0x0004;
        private static readonly ConditionalWeakTable<byte[], byte[]> Copies = new();
        private static byte[]? _playing;

        public static void Play(byte[] wave)
        {
            var sound = Copies.GetValue(wave, original =>
            {
                var copy = GC.AllocateArray<byte>(original.Length, pinned: true);
                original.CopyTo(copy, 0);
                return copy;
            });
            PlaySound(sound, IntPtr.Zero, Async | NoDefault | Memory);
            _playing = sound;
        }

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
    }
}
