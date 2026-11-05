using System.Windows;
using System.Windows.Markup;
using CommandCenter.Services;

namespace CommandCenter.Localization
{
    // The parts of the language setting (Loc) that only WPF needs: reading direction, text alignment and message box
    // options. Phase 2 removes them together with Arabic.
    public static class LocUi
    {
        public static FlowDirection FlowDirection => Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        // Names and other text that stays left to right (with FlowDirection LeftToRight) still starts on the reading side
        // of the line, so a long name is cut at its end: "A lagoon Too Far [20…" instead of "…A lagoon Too Far [2".
        public static TextAlignment StartAlignment => Loc.IsRightToLeft ? TextAlignment.Right : TextAlignment.Left;

        public static MessageBoxOptions MessageBoxOptions => Loc.IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
    }

    // {l:T Play online}, {l:T 'Text with a {0} at the start'} or {l:T Text='...'}, with
    // xmlns:l="clr-namespace:CommandCenter.Localization".
    // A comma ends an argument in XAML markup, so "{l:T Hello, world}" arrives in parts and is joined again with ", ".
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TExtension : MarkupExtension
    {
        public TExtension() { }
        public TExtension(string text) => Text = text;
        public TExtension(string a, string b) => Text = a + ", " + b;
        public TExtension(string a, string b, string c) => Text = a + ", " + b + ", " + c;
        public TExtension(string a, string b, string c, string d) => Text = a + ", " + b + ", " + c + ", " + d;

        public string Text { get; set; } = "";

        public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
    }
}
