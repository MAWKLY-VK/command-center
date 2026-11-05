namespace CommandCenter.Core.Imaging
{
    // A decoded picture: straight (not premultiplied) BGRA bytes, top row first. The services work with these; the App
    // turns them into WPF bitmaps (Imaging/Bitmaps.cs).
    public sealed record DecodedImage(int Width, int Height, byte[] Pixels);

    // The largest pictures Command Center decodes (spec phase 3): map previews, and the game's textures
    public static class ImageLimits
    {
        public const int Preview = 1024;
        public const int Texture = 4096;
    }

    // A rectangle of pixels in a picture. Empty is all zeros, like WPF's Int32Rect.Empty.
    public readonly record struct ImageRect(int X, int Y, int Width, int Height)
    {
        public static ImageRect Empty => default;

        public bool IsEmpty => X == 0 && Y == 0 && Width == 0 && Height == 0;
    }
}
