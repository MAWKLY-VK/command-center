using System.IO;
using System.Windows.Media;

namespace CommandCenter.Localization
{
    // Arial's character widths for Arabic shaping (ArabicShaping.ArialAdvance), read with WPF from the Windows font.
    // Phase 2 removes it together with Arabic shaping.
    internal static class ArialWidths
    {
        private static readonly Lazy<GlyphTypeface?> Arial = new(() =>
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
                return File.Exists(path) ? new GlyphTypeface(new Uri(path)) : null;
            }
            catch
            {
                return null;
            }
        });

        // In em; null when the font is missing or lacks the character
        public static double? Advance(char c) =>
            Arial.Value is { } font && font.CharacterToGlyphMap.TryGetValue(c, out ushort glyph) ? font.AdvanceWidths[glyph] : null;
    }
}
