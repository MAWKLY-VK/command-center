using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandCenter.Imaging
{
    // The control bar's pictures as WPF bitmaps: cut out of the atlases that GameImages finds, and turned upright when
    // the atlas holds them rotated. Each GameImages keeps its own bitmaps for as long as it lives.
    public static class GameImageBitmaps
    {
        private sealed class Cache
        {
            public readonly Dictionary<string, BitmapSource?> Textures = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, BitmapSource?> Pictures = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly ConditionalWeakTable<GameImages, Cache> Caches = new();

        // The picture as the control bar shows it (turned upright when the atlas holds it rotated)
        public static BitmapSource? Get(this GameImages images, string? mappedImage)
        {
            if (string.IsNullOrEmpty(mappedImage))
                return null;
            Cache cache = Caches.GetValue(images, _ => new Cache());
            if (cache.Pictures.TryGetValue(mappedImage, out var cached))
                return cached;

            BitmapSource? result = null;
            try
            {
                if (images.Find(mappedImage) is { } map && images.Texture(map.Texture) is { } atlas
                    && GameImages.PixelRect(map, atlas.PixelWidth, atlas.PixelHeight) is { } rect)
                {
                    BitmapSource crop = new CroppedBitmap(atlas, rect.ToInt32Rect());
                    if (map.Rotated)
                        crop = new TransformedBitmap(crop, new RotateTransform(-90));
                    crop.Freeze();
                    result = crop;
                }
            }
            catch
            {
                result = null;
            }
            cache.Pictures[mappedImage] = result;
            return result;
        }

        // A whole texture as the game would load it
        public static BitmapSource? Texture(this GameImages images, string name)
        {
            Cache cache = Caches.GetValue(images, _ => new Cache());
            if (cache.Textures.TryGetValue(name, out var cached))
                return cached;

            BitmapSource? image = null;
            try
            {
                image = images.TexturePixels(name)?.ToBitmap();
            }
            catch
            {
                image = null;
            }
            cache.Textures[name] = image;
            return image;
        }
    }
}
