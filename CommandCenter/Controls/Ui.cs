using System.Windows;
using System.Windows.Media;

namespace CommandCenter.Controls
{
    public enum Variant { Secondary, Primary, Ghost, Danger }

    // Attached settings read by the control templates in Theme/Controls.xaml.
    public static class Ui
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));
        public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);
        public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);

        public static readonly DependencyProperty IconFilledProperty = DependencyProperty.RegisterAttached(
            "IconFilled", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
        public static bool GetIconFilled(DependencyObject d) => (bool)d.GetValue(IconFilledProperty);
        public static void SetIconFilled(DependencyObject d, bool value) => d.SetValue(IconFilledProperty, value);

        public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached(
            "Badge", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
        public static string? GetBadge(DependencyObject d) => (string?)d.GetValue(BadgeProperty);
        public static void SetBadge(DependencyObject d, string? value) => d.SetValue(BadgeProperty, value);

        public static readonly DependencyProperty BadgeMarkProperty = DependencyProperty.RegisterAttached(
            "BadgeMark", typeof(Mark), typeof(Ui), new FrameworkPropertyMetadata(Mark.None));
        public static Mark GetBadgeMark(DependencyObject d) => (Mark)d.GetValue(BadgeMarkProperty);
        public static void SetBadgeMark(DependencyObject d, Mark value) => d.SetValue(BadgeMarkProperty, value);

        public static readonly DependencyProperty VariantProperty = DependencyProperty.RegisterAttached(
            "Variant", typeof(Variant), typeof(Ui), new FrameworkPropertyMetadata(Variant.Secondary));
        public static Variant GetVariant(DependencyObject d) => (Variant)d.GetValue(VariantProperty);
        public static void SetVariant(DependencyObject d, Variant value) => d.SetValue(VariantProperty, value);

        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
            "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
        public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);
        public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);

        public static readonly DependencyProperty SubtitleProperty = DependencyProperty.RegisterAttached(
            "Subtitle", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
        public static string? GetSubtitle(DependencyObject d) => (string?)d.GetValue(SubtitleProperty);
        public static void SetSubtitle(DependencyObject d, string? value) => d.SetValue(SubtitleProperty, value);

        public static readonly DependencyProperty CutProperty = DependencyProperty.RegisterAttached(
            "Cut", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(9.0));
        public static double GetCut(DependencyObject d) => (double)d.GetValue(CutProperty);
        public static void SetCut(DependencyObject d, double value) => d.SetValue(CutProperty, value);
    }
}
