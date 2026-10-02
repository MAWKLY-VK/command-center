using System.Text;

namespace CommandCenter.Services
{
    // A small DXT1/DXT3/DXT5 block encoder (principal axis range fit), and the .dds layout the game's loader reads
    // (DDSFileClass: a 124-byte surface description, a DXT FourCC, then the levels).
    public static class Dxt
    {
        // Alpha of a DXT5 alpha index
        public static int AlphaValue(int a0, int a1, int index) => index switch
        {
            0 => a0,
            1 => a1,
            _ when a0 > a1 => ((8 - index) * a0 + (index - 1) * a1) / 7,
            6 => 0,
            7 => 255,
            _ => ((6 - index) * a0 + (index - 1) * a1) / 5,
        };

        // Encodes a 4x4 block of straight BGRA pixels (64 bytes, row by row) into 8 (DXT1) or 16 (DXT3, DXT5) bytes
        public static void EncodeBlock(ReadOnlySpan<byte> px, Span<byte> dest, string fourcc)
        {
            switch (fourcc)
            {
                case "DXT1":
                    EncodeColor(px, dest, punchThrough: true);
                    break;
                case "DXT3":
                    for (int i = 0; i < 8; i++)
                    {
                        int lo = (px[(2 * i) * 4 + 3] + 8) / 17, hi = (px[(2 * i + 1) * 4 + 3] + 8) / 17;
                        dest[i] = (byte)(lo | hi << 4);
                    }
                    EncodeColor(px, dest[8..], punchThrough: false);
                    break;
                default:
                    EncodeAlpha(px, dest);
                    EncodeColor(px, dest[8..], punchThrough: false);
                    break;
            }
        }

        private static void EncodeAlpha(ReadOnlySpan<byte> px, Span<byte> dest)
        {
            int min = 255, max = 0;
            for (int i = 0; i < 16; i++)
            {
                min = Math.Min(min, px[i * 4 + 3]);
                max = Math.Max(max, px[i * 4 + 3]);
            }
            dest[0] = (byte)max;
            dest[1] = (byte)min;
            ulong bits = 0;
            if (max != min)
            {
                for (int i = 0; i < 16; i++)
                {
                    int a = px[i * 4 + 3], best = 0, bestError = int.MaxValue;
                    for (int k = 0; k < 8; k++)
                    {
                        int error = Math.Abs(AlphaValue(max, min, k) - a);
                        if (error < bestError) { bestError = error; best = k; }
                    }
                    bits |= (ulong)best << (3 * i);
                }
            }
            for (int i = 0; i < 6; i++)
                dest[2 + i] = (byte)(bits >> (8 * i));
        }

        // Colour endpoints at the ends of the pixels' main direction; in DXT1 a pixel under half alpha becomes transparent
        private static void EncodeColor(ReadOnlySpan<byte> px, Span<byte> dest, bool punchThrough)
        {
            var points = new List<(double R, double G, double B)>(16);
            bool anyTransparent = false;
            for (int i = 0; i < 16; i++)
            {
                if (punchThrough && px[i * 4 + 3] < 128)
                    anyTransparent = true;
                else
                    points.Add((px[i * 4 + 2], px[i * 4 + 1], px[i * 4]));
            }

            ushort c0 = 0, c1 = 0;
            if (points.Count > 0)
            {
                double mr = points.Average(p => p.R), mg = points.Average(p => p.G), mb = points.Average(p => p.B);
                double rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;
                foreach (var p in points)
                {
                    double r = p.R - mr, g = p.G - mg, bl = p.B - mb;
                    rr += r * r; rg += r * g; rb += r * bl; gg += g * g; gb += g * bl; bb += bl * bl;
                }
                // Power iteration for the main axis, starting from the widest channel
                (double X, double Y, double Z) axis = rr >= gg && rr >= bb ? (1, 0, 0) : gg >= bb ? (0, 1, 0) : (0, 0, 1);
                for (int i = 0; i < 8; i++)
                {
                    var next = (rr * axis.X + rg * axis.Y + rb * axis.Z, rg * axis.X + gg * axis.Y + gb * axis.Z, rb * axis.X + gb * axis.Y + bb * axis.Z);
                    double length = Math.Sqrt(next.Item1 * next.Item1 + next.Item2 * next.Item2 + next.Item3 * next.Item3);
                    if (length < 1e-9)
                        break;
                    axis = (next.Item1 / length, next.Item2 / length, next.Item3 / length);
                }
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var p in points)
                {
                    double t = (p.R - mr) * axis.X + (p.G - mg) * axis.Y + (p.B - mb) * axis.Z;
                    lo = Math.Min(lo, t);
                    hi = Math.Max(hi, t);
                }
                c0 = To565(mr + axis.X * hi, mg + axis.Y * hi, mb + axis.Z * hi);
                c1 = To565(mr + axis.X * lo, mg + axis.Y * lo, mb + axis.Z * lo);
            }

            // Four colours need c0 > c1; three colours plus transparent need c0 <= c1
            bool threeColour = anyTransparent;
            if (threeColour ? c0 > c1 : c0 < c1)
                (c0, c1) = (c1, c0);

            var a = Dds.Rgb565(c0);
            var b = Dds.Rgb565(c1);
            var palette = new (int R, int G, int B)[4];
            palette[0] = a;
            palette[1] = b;
            int colours;
            if (!threeColour && c0 > c1)
            {
                palette[2] = ((2 * a.R + b.R) / 3, (2 * a.G + b.G) / 3, (2 * a.B + b.B) / 3);
                palette[3] = ((a.R + 2 * b.R) / 3, (a.G + 2 * b.G) / 3, (a.B + 2 * b.B) / 3);
                colours = 4;
            }
            else
            {
                // Also the case c0 == c1 without transparency: index 0 is enough
                palette[2] = ((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2);
                colours = 3;
            }

            uint indices = 0;
            for (int i = 0; i < 16; i++)
            {
                int index;
                if (threeColour && px[i * 4 + 3] < 128)
                    index = 3;
                else
                {
                    int r = px[i * 4 + 2], g = px[i * 4 + 1], bl = px[i * 4];
                    index = 0;
                    int bestError = int.MaxValue;
                    for (int k = 0; k < colours; k++)
                    {
                        int dr = palette[k].R - r, dg = palette[k].G - g, db = palette[k].B - bl;
                        int error = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
                        if (error < bestError) { bestError = error; index = k; }
                    }
                }
                indices |= (uint)index << (2 * i);
            }

            dest[0] = (byte)c0; dest[1] = (byte)(c0 >> 8);
            dest[2] = (byte)c1; dest[3] = (byte)(c1 >> 8);
            dest[4] = (byte)indices; dest[5] = (byte)(indices >> 8); dest[6] = (byte)(indices >> 16); dest[7] = (byte)(indices >> 24);
        }

        private static ushort To565(double r, double g, double b)
        {
            int R = (int)Math.Round(Math.Clamp(r, 0, 255) * 31 / 255), G = (int)Math.Round(Math.Clamp(g, 0, 255) * 63 / 255), B = (int)Math.Round(Math.Clamp(b, 0, 255) * 31 / 255);
            return (ushort)(R << 11 | G << 5 | B);
        }

        // A one-level .dds holding the given DXT data; marker (up to 16 bytes) goes into an unused part of the header
        public static byte[] WriteDds(int width, int height, string fourcc, byte[] level, string marker)
        {
            byte[] file = new byte[Dds.HeaderSize + level.Length];
            Encoding.ASCII.GetBytes("DDS ").CopyTo(file, 0);
            void Put(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(file, offset);
            Put(4, 124);                                       // size of the description
            Put(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000); // caps, height, width, pixel format, mip count, linear size
            Put(12, (uint)height);
            Put(16, (uint)width);
            Put(20, (uint)level.Length);
            Put(28, 1);                                        // one level
            Encoding.ASCII.GetBytes(marker[..Math.Min(16, marker.Length)]).CopyTo(file, 60);
            Put(76, 32);                                       // pixel format: size, flags (FourCC), FourCC
            Put(80, 0x4);
            Encoding.ASCII.GetBytes(fourcc).CopyTo(file, 84);
            Put(108, 0x1000);                                  // a plain texture
            level.CopyTo(file, Dds.HeaderSize);
            return file;
        }
    }
}
