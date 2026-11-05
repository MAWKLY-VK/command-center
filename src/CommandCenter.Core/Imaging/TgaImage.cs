using System.IO;

namespace CommandCenter.Core.Imaging
{
    // Decodes the game's .tga pictures, map previews among them (24 or 32 bit, raw or RLE), into plain pixels. A picture
    // larger than its limit, or whose size needs more data than it holds, is refused before anything is allocated for it.
    public static class TgaImage
    {
        // A map preview, from its own file. A damaged one is skipped and written to the error log.
        public static DecodedImage? Load(string path)
        {
            try
            {
                // Raw 32-bit pixels at the limit, with room for run-length headers and the file's own header and footer
                long fileLimit = 2L * ImageLimits.Preview * ImageLimits.Preview * 4;
                return Decode(BoundedFile.Read(path, fileLimit), keepAlpha: false, ImageLimits.Preview, path);
            }
            catch (InvalidDataException ex)
            {
                ErrorLog.Skipped(path, ex.Message);
                return null;
            }
            catch
            {
                return null;
            }
        }

        // Null when the picture is not one the game reads, or is damaged; a damaged one is written to the error log
        // under source, when given
        public static DecodedImage? Decode(byte[] b, bool keepAlpha = false, int maxSide = ImageLimits.Texture, string? source = null)
        {
            try
            {
                return Read(b, keepAlpha, maxSide);
            }
            catch (InvalidDataException ex)
            {
                if (source != null)
                    ErrorLog.Skipped(source, ex.Message);
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static DecodedImage? Read(byte[] b, bool keepAlpha, int maxSide)
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
            if (width > maxSide || height > maxSide)
                throw new InvalidDataException($"The picture is {width} x {height}, larger than {maxSide} x {maxSide}.");

            // Raw pixels are all in the file; a run-length packet of 1 + bytesPerPixel bytes stands for at most 128
            int bytesPerPixel = bitsPerPixel / 8;
            int pos = 18 + idLength;
            long rawLength = (long)width * height * bytesPerPixel;
            long data = b.Length - pos;
            long most = imageType == 2 ? data : (data / (1 + bytesPerPixel) + 1) * 128 * bytesPerPixel;
            if (data < 0 || rawLength > most)
                throw new InvalidDataException("The picture holds less data than its size needs.");

            byte[] raw = new byte[rawLength];
            if (imageType == 2)
            {
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

            return new DecodedImage(width, height, pixels);
        }
    }
}
