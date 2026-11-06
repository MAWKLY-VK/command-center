using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CommandCenter.Core.Game
{
    // The game's WAV sounds as plain 16-bit PCM, the one form every Windows player takes (ticket 14). The game keeps its
    // short interface sounds as IMA ADPCM and longer ones as PCM; both come out as PCM at the volume asked for. Anything
    // else gives null.
    public static class WaveFiles
    {
        private const ushort Pcm = 1;
        private const ushort ImaAdpcm = 0x11;

        private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

        private static readonly int[] StepTable =
        [
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
            1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
            7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
        ];

        private sealed record Format(ushort Tag, ushort Channels, int Rate, ushort BlockAlign, ushort Bits, int SamplesPerBlock);

        // volume: 0 to 1, the share of full volume the game plays the sound at
        public static byte[]? ToPcm16(byte[] wave, double volume)
        {
            if (Parse(wave) is not var (format, data))
                return null;
            short[]? samples = format switch
            {
                { Tag: Pcm, Bits: 16 } => Pcm16(data),
                { Tag: Pcm, Bits: 8 } => data.Select(b => (short)((b - 128) << 8)).ToArray(),
                { Tag: ImaAdpcm, Bits: 4 } => DecodeIma(data, format),
                _ => null,
            };
            if (samples == null || samples.Length == 0)
                return null;
            double share = Math.Clamp(volume, 0, 1);
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Clamp(Math.Round(samples[i] * share), short.MinValue, short.MaxValue);
            return Write(samples, format.Channels, format.Rate);
        }

        // The fmt and data chunks of a RIFF WAVE file; null when either is missing or cut short
        private static (Format, byte[])? Parse(byte[] wave)
        {
            if (wave.Length < 12 || Encoding.ASCII.GetString(wave, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wave, 8, 4) != "WAVE")
                return null;
            Format? format = null;
            byte[]? data = null;
            int at = 12;
            while (at + 8 <= wave.Length)
            {
                string id = Encoding.ASCII.GetString(wave, at, 4);
                int size = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(at + 4));
                if (size < 0 || at + 8 + (long)size > wave.Length)
                    return null;
                var body = wave.AsSpan(at + 8, size);
                if (id == "fmt " && size >= 16)
                {
                    int samplesPerBlock = size >= 20 ? BinaryPrimitives.ReadUInt16LittleEndian(body[18..]) : 0;
                    format = new Format(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
                        BinaryPrimitives.ReadInt32LittleEndian(body[4..]), BinaryPrimitives.ReadUInt16LittleEndian(body[12..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(body[14..]), samplesPerBlock);
                }
                else if (id == "data")
                {
                    data = body.ToArray();
                }
                at += 8 + size + (size & 1);
            }
            if (format is not { Channels: > 0 and <= 8, Rate: > 0 } || data == null)
                return null;
            return (format, data);
        }

        private static short[] Pcm16(byte[] data)
        {
            var samples = new short[data.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2));
            return samples;
        }

        // Microsoft's IMA ADPCM: blocks of BlockAlign bytes, each starting with a 4-byte header per channel (the first
        // sample and the step index), then 4-bit codes, low nibble first, in runs of 4 bytes per channel
        private static short[]? DecodeIma(byte[] data, Format format)
        {
            int channels = format.Channels;
            int header = 4 * channels;
            if (format.BlockAlign <= header)
                return null;
            int perBlock = format.SamplesPerBlock > 0 ? format.SamplesPerBlock : (format.BlockAlign - header) * 2 / channels + 1;
            var output = new List<short>(data.Length * 2);
            var predictor = new int[channels];
            var index = new int[channels];
            var decoded = new List<short>[channels];
            for (int start = 0; start + header <= data.Length; start += format.BlockAlign)
            {
                int end = Math.Min(start + format.BlockAlign, data.Length);
                for (int c = 0; c < channels; c++)
                {
                    predictor[c] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(start + 4 * c));
                    index[c] = Math.Clamp((int)data[start + 4 * c + 2], 0, StepTable.Length - 1);
                    decoded[c] = [(short)predictor[c]];
                }
                for (int at = start + header; at + 4 * channels <= end; at += 4 * channels)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        for (int b = 0; b < 4; b++)
                        {
                            byte pair = data[at + 4 * c + b];
                            decoded[c].Add(Step(pair & 0x0F, ref predictor[c], ref index[c]));
                            decoded[c].Add(Step(pair >> 4, ref predictor[c], ref index[c]));
                        }
                    }
                }
                // A mono block may end with fewer than four code bytes
                if (channels == 1)
                {
                    int rest = (end - start - header) % 4;
                    for (int at = end - rest; at < end; at++)
                    {
                        decoded[0].Add(Step(data[at] & 0x0F, ref predictor[0], ref index[0]));
                        decoded[0].Add(Step(data[at] >> 4, ref predictor[0], ref index[0]));
                    }
                }
                int count = Math.Min(perBlock, decoded.Min(d => d.Count));
                for (int i = 0; i < count; i++)
                {
                    for (int c = 0; c < channels; c++)
                        output.Add(decoded[c][i]);
                }
            }
            return output.ToArray();
        }

        private static short Step(int code, ref int predictor, ref int index)
        {
            int step = StepTable[index];
            int difference = step >> 3;
            if ((code & 4) != 0)
                difference += step;
            if ((code & 2) != 0)
                difference += step >> 1;
            if ((code & 1) != 0)
                difference += step >> 2;
            predictor = Math.Clamp((code & 8) != 0 ? predictor - difference : predictor + difference, short.MinValue, short.MaxValue);
            index = Math.Clamp(index + IndexTable[code], 0, StepTable.Length - 1);
            return (short)predictor;
        }

        private static byte[] Write(short[] samples, ushort channels, int rate)
        {
            using var stream = new MemoryStream(44 + samples.Length * 2);
            using var w = new BinaryWriter(stream);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + samples.Length * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write(Pcm);
            w.Write(channels);
            w.Write(rate);
            w.Write(rate * channels * 2);
            w.Write((ushort)(channels * 2));
            w.Write((ushort)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(samples.Length * 2);
            foreach (short sample in samples)
                w.Write(sample);
            w.Flush();
            return stream.ToArray();
        }
    }
}
