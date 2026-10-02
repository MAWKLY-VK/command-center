using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandCenter.Services
{
    // Loads the small preview images that ship with maps (.tga, 24 or 32 bit, raw or RLE).
    public static class TgaImage
    {
        public static BitmapSource? Load(string path)
        {
            try { return Decode(File.ReadAllBytes(path)); }
            catch { return null; }
        }

        public static BitmapSource? Decode(byte[] b, bool keepAlpha = false)
        {
            try
            {
                if (b.Length < 18)
                    return null;

                int idLength = b[0];
                int colorMapType = b[1];
                int imageType = b[2];
                int width = b[12] | (b[13] << 8);
                int height = b[14] | (b[15] << 8);
                int bitsPerPixel = b[16];
                bool topDown = (b[17] & 0x20) != 0;

                if (colorMapType != 0 || (imageType != 2 && imageType != 10) || (bitsPerPixel != 24 && bitsPerPixel != 32) || width == 0 || height == 0)
                    return null;

                int bytesPerPixel = bitsPerPixel / 8;
                int pos = 18 + idLength;
                byte[] raw = new byte[width * height * bytesPerPixel];

                if (imageType == 2)
                {
                    if (pos + raw.Length > b.Length)
                        return null;
                    Buffer.BlockCopy(b, pos, raw, 0, raw.Length);
                }
                else
                {
                    int outPos = 0;
                    while (outPos < raw.Length && pos < b.Length)
                    {
                        int header = b[pos++];
                        int count = (header & 0x7F) + 1;
                        if ((header & 0x80) != 0)
                        {
                            if (pos + bytesPerPixel > b.Length)
                                return null;
                            for (int i = 0; i < count && outPos < raw.Length; i++)
                            {
                                Buffer.BlockCopy(b, pos, raw, outPos, bytesPerPixel);
                                outPos += bytesPerPixel;
                            }
                            pos += bytesPerPixel;
                        }
                        else
                        {
                            int length = Math.Min(count * bytesPerPixel, raw.Length - outPos);
                            if (pos + length > b.Length)
                                return null;
                            Buffer.BlockCopy(b, pos, raw, outPos, length);
                            outPos += length;
                            pos += count * bytesPerPixel;
                        }
                    }
                }

                byte[] pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                {
                    int sourceRow = topDown ? y : height - 1 - y;
                    for (int x = 0; x < width; x++)
                    {
                        int s = (sourceRow * width + x) * bytesPerPixel;
                        int d = (y * width + x) * 4;
                        pixels[d] = raw[s];
                        pixels[d + 1] = raw[s + 1];
                        pixels[d + 2] = raw[s + 2];
                        pixels[d + 3] = keepAlpha && bytesPerPixel == 4 ? raw[s + 3] : (byte)255;
                    }
                }

                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
