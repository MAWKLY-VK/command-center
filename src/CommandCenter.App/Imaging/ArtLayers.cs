using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandCenter.Imaging
{
    // The fine grain over the art (plan 3-c: noise at a few per cent keeps the scrims' gradients from banding). The tile
    // is the same at every run, so screenshots of two builds compare.
    public static class ArtLayers
    {
        private const int Tile = 128;

        public static ImageBrush Grain()
        {
            var noise = new byte[Tile * Tile];
            new Random(1).NextBytes(noise);
            var bitmap = BitmapSource.Create(Tile, Tile, 96, 96, PixelFormats.Gray8, null, noise, Tile);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, Tile, Tile),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
            };
            brush.Freeze();
            return brush;
        }
    }
}
