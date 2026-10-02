using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandCenter.Services
{
    // The game's own faction and general pictures, cut out of its interface atlases (see GameImages).
    // The images are frozen, so they can be created on any thread and shown anywhere.
    public static class FactionArt
    {
        // Same MappedImage names as PlayerTemplate.ini: GeneralImage (a 48 x 48 outline of the general's badge,
        // drawn in black) and SideIconImage (the 24 x 22 colored square the lobby shows next to a player)
        private static readonly Dictionary<(Faction, string), string> Emblems = new()
        {
            [(Faction.Usa, "")] = "USA_Logo",
            [(Faction.Usa, "superweapon")] = "USA_Superweapon",
            [(Faction.Usa, "laser")] = "USA_Laser",
            [(Faction.Usa, "airforce")] = "USA_Air",
            [(Faction.China, "")] = "China_Logo",
            [(Faction.China, "tank")] = "China_Tank",
            [(Faction.China, "infantry")] = "China_Infantry",
            [(Faction.China, "nuke")] = "China_Nuke",
            [(Faction.Gla, "")] = "GLA_Logo",
            [(Faction.Gla, "toxin")] = "GLA_Toxin",
            [(Faction.Gla, "demolition")] = "GLA_Demo",
            [(Faction.Gla, "stealth")] = "GLA_Stealth",
        };

        private static readonly Lazy<GameImages?> Images = new(Open);
        private static readonly Dictionary<(string, uint), BitmapSource?> Cache = new();
        private static readonly object Gate = new();

        // Opening reads the game's image definitions, which takes a moment: call this from a background
        // task early so the first Emblem or Icon call does not stall the interface.
        public static Task PreloadAsync() => Task.Run(() => Images.Value);

        // The general's badge as a one color silhouette that keeps the game's soft edges, white by default
        // so it shows on the dark interface. Without a known general it is the faction's own badge.
        // Null for Random, Observer and anything else without art. Shown small, set
        // RenderOptions.BitmapScalingMode="Fant" on the Image.
        public static BitmapSource? Emblem(Faction faction, string general = "") => Emblem(faction, general, Colors.White);

        public static BitmapSource? Emblem(Faction faction, string general, Color tint)
        {
            string key = (general ?? "").Replace(" ", "").ToLowerInvariant();
            if (key.Length > 0 && Emblems.TryGetValue((faction, GeneralKey(key)), out string? name))
                return Tinted(name, tint);
            return Emblems.TryGetValue((faction, ""), out name) ? Tinted(name, tint) : null;
        }

        // The colored square from the lobby: blue for USA, red for China, green for GLA, purple for an observer.
        // Null for Random.
        public static BitmapSource? Icon(Faction faction, string general = "")
        {
            string? name = faction switch
            {
                Faction.Usa => "GameinfoAMRCA",
                Faction.China => "GameinfoCHINA",
                Faction.Gla => "GameinfoGLA",
                Faction.Observer => "GameinfoOBSRVR",
                Faction.Other when string.Equals(general, "Boss", StringComparison.OrdinalIgnoreCase) => "GameinfoBOSS",
                _ => null,
            };
            if (name == null)
                return null;
            lock (Gate)
                return Images.Value?.Get(name);
        }

        // "Air Force", "Air Force General", "Airforce" and the like all mean the same general
        private static string GeneralKey(string key) =>
            key.Contains("air") ? "airforce"
            : key.Contains("super") ? "superweapon"
            : key.Contains("laser") ? "laser"
            : key.Contains("tank") ? "tank"
            : key.Contains("infantry") ? "infantry"
            : key.Contains("nuke") ? "nuke"
            : key.Contains("toxin") || key.Contains("chem") ? "toxin"
            : key.Contains("demo") ? "demolition"
            : key.Contains("stealth") ? "stealth"
            : key;

        private static GameImages? Open()
        {
            try
            {
                // The faction art is the same in every language; GameImages falls back to the English textures
                return new GameImages(new GameFiles(GamePaths.Game), "English");
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource? Tinted(string name, Color tint)
        {
            uint rgb = (uint)(tint.R << 16 | tint.G << 8 | tint.B);
            lock (Gate)
            {
                if (Cache.TryGetValue((name, rgb), out var cached))
                    return cached;

                BitmapSource? result = null;
                try
                {
                    if (Images.Value?.Get(name) is { } source)
                    {
                        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                        int width = bgra.PixelWidth, height = bgra.PixelHeight;
                        byte[] pixels = new byte[width * height * 4];
                        bgra.CopyPixels(pixels, width * 4, 0);
                        for (int i = 0; i < pixels.Length; i += 4)
                        {
                            pixels[i] = tint.B;
                            pixels[i + 1] = tint.G;
                            pixels[i + 2] = tint.R;
                        }
                        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                        bitmap.Freeze();
                        result = bitmap;
                    }
                }
                catch
                {
                    result = null;
                }
                Cache[(name, rgb)] = result;
                return result;
            }
        }
    }
}
