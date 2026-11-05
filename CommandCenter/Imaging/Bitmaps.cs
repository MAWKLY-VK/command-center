using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Services;

namespace CommandCenter.Imaging
{
    // Turns decoded pixels from the services into WPF bitmaps. The bitmaps are frozen, so they can be made on any thread
    // and shown anywhere.
    public static class Bitmaps
    {
        public static BitmapSource ToBitmap(this DecodedImage image)
        {
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        public static Int32Rect ToInt32Rect(this ImageRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

        // A map preview (.tga) as a bitmap, or null when it is missing or unreadable
        public static BitmapSource? LoadTga(string path) => TgaImage.Load(path)?.ToBitmap();
    }
}
