using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CommandCenter.Controls
{
    // A line icon from Theme/Icons.xaml, drawn in the inherited text colour.
    public class Icon : FrameworkElement
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
            nameof(Filled), typeof(bool), typeof(Icon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

        public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
        public double Stroke { get => (double)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

        public Icon()
        {
            Width = 20;
            Height = 20;
            SnapsToDevicePixels = false;
        }

        // Arrows that point along the reading direction turn round in a right-to-left window; all other icons keep their drawing
        private static HashSet<Geometry>? _directional;

        private static bool IsDirectional(Geometry data)
        {
            _directional ??= new[] { "I.back", "I.chev", "I.undo" }
                .Select(key => Application.Current?.TryFindResource(key) as Geometry)
                .OfType<Geometry>()
                .ToHashSet();
            return _directional.Contains(data);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Data == null)
                return;
            double size = Math.Min(ActualWidth, ActualHeight);
            bool unmirror = FlowDirection == FlowDirection.RightToLeft && !IsDirectional(Data);
            if (unmirror)
                dc.PushTransform(Rtl.Unmirror(size));
            double scale = size / 24.0;
            dc.PushTransform(new ScaleTransform(scale, scale));
            if (Filled)
            {
                dc.DrawGeometry(Foreground, null, Data);
            }
            else
            {
                var pen = new Pen(Foreground, Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, Data);
            }
            dc.Pop();
            if (unmirror)
                dc.Pop();
        }
    }

    // In a right-to-left window everything below the window is drawn mirrored. Drawings that must keep
    // their orientation (icons, ticks, Latin glyph runs) push this transform first to turn it back.
    public static class Rtl
    {
        public static Transform Unmirror(double width) => new MatrixTransform(-1, 0, 0, 1, width, 0);
    }

    public enum Mark { None, Ok, Warn, Danger, Info, Changed }

    // Status never relies on colour alone: a green disc with a tick, a yellow triangle with "!",
    // a red disc with a cross, a blue disc with "i", or a blue diamond for "changed".
    public class StatusMark : FrameworkElement
    {
        public static readonly DependencyProperty MarkProperty = DependencyProperty.Register(
            nameof(Mark), typeof(Mark), typeof(StatusMark), new FrameworkPropertyMetadata(Mark.None, FrameworkPropertyMetadataOptions.AffectsRender));

        public Mark Mark { get => (Mark)GetValue(MarkProperty); set => SetValue(MarkProperty, value); }

        private static readonly Brush Ink = Frozen(Color.FromRgb(0x10, 0x11, 0x12));

        public StatusMark()
        {
            Width = 16;
            Height = 16;
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Brush BrushFor(Mark mark) => mark switch
        {
            Mark.Ok => (Brush)Application.Current.FindResource("Ok"),
            Mark.Warn => (Brush)Application.Current.FindResource("Warn"),
            Mark.Danger => (Brush)Application.Current.FindResource("Danger"),
            Mark.Info or Mark.Changed => (Brush)Application.Current.FindResource("Info"),
            _ => Brushes.Transparent,
        };

        protected override void OnRender(DrawingContext dc)
        {
            double s = Math.Min(ActualWidth, ActualHeight);
            if (s <= 0 || Mark == Mark.None)
                return;
            var fill = BrushFor(Mark);
            double c = s / 2;
            var ink = new Pen(Ink, Math.Max(1.4, s * 0.13)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            // A tick stays a tick in a right-to-left window
            bool unmirror = FlowDirection == FlowDirection.RightToLeft;
            if (unmirror)
                dc.PushTransform(Rtl.Unmirror(s));

            switch (Mark)
            {
                case Mark.Ok:
                    dc.DrawEllipse(fill, null, new Point(c, c), c, c);
                    dc.DrawGeometry(null, ink, Geometry.Parse(F("M{0},{1} L{2},{3} L{4},{5}", s * .28, s * .52, s * .44, s * .68, s * .73, s * .35)));
                    break;
                case Mark.Warn:
                    dc.DrawGeometry(fill, null, Geometry.Parse(F("M{0},{1} L{2},{3} L{4},{5} Z", c, s * .04, s, s * .94, 0, s * .94)));
                    dc.DrawLine(ink, new Point(c, s * .40), new Point(c, s * .64));
                    dc.DrawEllipse(Ink, null, new Point(c, s * .80), s * .065, s * .065);
                    break;
                case Mark.Danger:
                    dc.DrawEllipse(fill, null, new Point(c, c), c, c);
                    dc.DrawLine(ink, new Point(s * .33, s * .33), new Point(s * .67, s * .67));
                    dc.DrawLine(ink, new Point(s * .67, s * .33), new Point(s * .33, s * .67));
                    break;
                case Mark.Info:
                    dc.DrawEllipse(fill, null, new Point(c, c), c, c);
                    dc.DrawLine(ink, new Point(c, s * .45), new Point(c, s * .72));
                    dc.DrawEllipse(Ink, null, new Point(c, s * .29), s * .065, s * .065);
                    break;
                case Mark.Changed:
                    dc.DrawGeometry(fill, null, Geometry.Parse(F("M{0},{1} L{2},{3} L{4},{5} L{6},{7} Z", c, s * .1, s * .9, c, c, s * .9, s * .1, c)));
                    break;
            }
            if (unmirror)
                dc.Pop();
        }

        private static string F(string format, params double[] values) =>
            string.Format(CultureInfo.InvariantCulture, format, values.Cast<object>().ToArray());
    }

    // The health gauge: a ring split into passed, warning and problem arcs with the score in the middle.
    public class ScoreRing : FrameworkElement
    {
        public static readonly DependencyProperty PassedProperty = DependencyProperty.Register(
            nameof(Passed), typeof(int), typeof(ScoreRing), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty WarningsProperty = DependencyProperty.Register(
            nameof(Warnings), typeof(int), typeof(ScoreRing), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ProblemsProperty = DependencyProperty.Register(
            nameof(Problems), typeof(int), typeof(ScoreRing), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(ScoreRing), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public int Passed { get => (int)GetValue(PassedProperty); set => SetValue(PassedProperty, value); }
        public int Warnings { get => (int)GetValue(WarningsProperty); set => SetValue(WarningsProperty, value); }
        public int Problems { get => (int)GetValue(ProblemsProperty); set => SetValue(ProblemsProperty, value); }
        public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            double s = Math.Min(ActualWidth, ActualHeight);
            int total = Passed + Warnings + Problems;
            if (s <= 0)
                return;
            double r = (s - Thickness) / 2;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x24, 0x28, 0x2B)), Thickness), center, r, r);
            if (total == 0)
                return;

            double start = -90;
            void Arc(int count, Mark mark)
            {
                if (count <= 0) return;
                double sweep = 360.0 * count / total;
                double gap = total > count ? 3 : 0;
                DrawArc(dc, center, r, start + gap / 2, sweep - gap, new Pen(StatusMark.BrushFor(mark), Thickness));
                start += sweep;
            }
            Arc(Passed, Mark.Ok);
            Arc(Warnings, Mark.Warn);
            Arc(Problems, Mark.Danger);
        }

        private static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
        {
            if (sweepDeg >= 359.9)
            {
                dc.DrawEllipse(null, pen, c, r, r);
                return;
            }
            double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false, false);
                ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    // Upper-case condensed text with letter spacing, which WPF text elements cannot do on their own.
    public class TrackedText : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(TrackedText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
            nameof(Tracking), typeof(double), typeof(TrackedText), new FrameworkPropertyMetadata(0.08, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty UpperProperty = DependencyProperty.Register(
            nameof(Upper), typeof(bool), typeof(TrackedText), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(TrackedText),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(TrackedText),
            new FrameworkPropertyMetadata(FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(TrackedText),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
        public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
        public bool Upper { get => (bool)GetValue(UpperProperty); set => SetValue(UpperProperty, value); }
        public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

        private static readonly FontFamily Family = new("Bahnschrift");

        // Bahnschrift has no Arabic letters; Segoe UI is the Windows font that has them
        private static readonly FontFamily ShapedFamily = new("Bahnschrift, Segoe UI");

        // Arabic letters look smaller than Latin capitals of the same size, most of all in the tiny section labels
        private double ArabicScale => FontSize <= 13 ? 1.2 : 1.12;

        // Right-to-left scripts (Hebrew, Arabic and their presentation forms)
        public static bool HasRtl(string text) => text.Any(c => c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF');

        private bool Shaped => HasRtl(Text ?? "");

        // Arabic has no capitals, and upper-casing the Latin words inside an Arabic phrase would look out of place
        private string Shown => Upper && !Shaped ? (Text ?? "").ToUpperInvariant() : Text ?? "";

        private GlyphTypeface? Glyphs()
        {
            var typeface = new Typeface(Family, FontStyles.Normal, FontWeight, FontStretches.Condensed);
            return typeface.TryGetGlyphTypeface(out var glyphs) ? glyphs : null;
        }

        // The tracked glyph run, or null when the text needs WPF's own layout (Arabic, or a character Bahnschrift lacks)
        private (ushort[] Indices, double[] Advances, double Width)? Layout()
        {
            string text = Visual(Shown);
            var glyphs = Glyphs();
            if (glyphs == null || text.Length == 0 || Shaped)
                return null;
            var indices = new ushort[text.Length];
            var advances = new double[text.Length];
            double width = 0, extra = Tracking * FontSize;
            for (int i = 0; i < text.Length; i++)
            {
                if (!glyphs.CharacterToGlyphMap.TryGetValue(text[i], out ushort index))
                    return null;
                indices[i] = index;
                advances[i] = glyphs.AdvanceWidths[index] * FontSize + (i < text.Length - 1 ? extra : 0);
                width += advances[i];
            }
            return (indices, advances, width);
        }

        // A glyph run is always drawn left to right. In a right-to-left paragraph the spaces and punctuation at either
        // end of a Latin phrase belong on the other side, as WPF's own text would place them ("/ Maps" shows as "Maps /").
        private string Visual(string text)
        {
            // Text from Loc.Ltr is meant to stay left to right as a whole
            bool embedded = text.Length > 1 && text[0] == Services.Loc.LtrMark && text[^1] == Services.Loc.LtrMark;
            if (text.Any(IsBidiControl))
                text = new string(text.Where(c => !IsBidiControl(c)).ToArray());
            if (FlowDirection != FlowDirection.RightToLeft || embedded)
                return text;
            int start = 0, end = text.Length;
            while (start < end && !char.IsLetterOrDigit(text[start]))
                start++;
            while (end > start && !char.IsLetterOrDigit(text[end - 1]))
                end--;
            if (start == end)
                return Mirrored(text);
            return Mirrored(text[end..]) + text[start..end] + Mirrored(text[..start]);
        }

        // Direction marks, embeddings and isolates: layout hints with no glyph of their own
        private static bool IsBidiControl(char c) => c is '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';

        private static string Mirrored(string part)
        {
            var chars = part.ToCharArray();
            Array.Reverse(chars);
            for (int i = 0; i < chars.Length; i++)
                chars[i] = chars[i] switch { '(' => ')', ')' => '(', '[' => ']', ']' => '[', '<' => '>', '>' => '<', '{' => '}', '}' => '{', _ => chars[i] };
            return new string(chars);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var glyphs = Glyphs();
            var layout = Layout();
            if (layout != null && glyphs != null)
                return new Size(Math.Ceiling(layout.Value.Width), Math.Ceiling(glyphs.Height * FontSize));
            if (Shown.Length == 0)
                return new Size(0, Math.Ceiling(glyphs != null ? glyphs.Height * FontSize : FontSize * 1.2));
            var text = Formatted();
            return new Size(Math.Ceiling(text.WidthIncludingTrailingWhitespace), Math.Ceiling(text.Height));
        }

        // Shaped text without tracking, laid out like a TextBlock in the element's own flow direction
        private FormattedText Formatted()
        {
            return new FormattedText(Shown, CultureInfo.CurrentUICulture, FlowDirection,
                new Typeface(ShapedFamily, FontStyles.Normal, FontWeight, FontStretches.Condensed), FontSize * (Shaped ? ArabicScale : 1), Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Shown.Length == 0)
                return;
            var layout = Layout();
            var glyphs = Glyphs();

            // In a right-to-left window draw unmirrored, so letters keep their shape and a line starts at the right edge
            bool rtl = FlowDirection == FlowDirection.RightToLeft;
            if (rtl)
                dc.PushTransform(Rtl.Unmirror(RenderSize.Width));

            if (layout == null || glyphs == null)
            {
                // Trimmed with an ellipsis only when the element really got less room than the text needs;
                // a right-to-left paragraph lines up against the right end of MaxTextWidth
                var text = Formatted();
                double box = Math.Max(1, RenderSize.Width), natural = text.WidthIncludingTrailingWhitespace;
                double max = box >= Math.Floor(natural) ? Math.Max(box, natural) + 1 : box;
                text.MaxTextWidth = max;
                text.MaxLineCount = 1;
                text.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(text, new Point(rtl ? box - max : 0, 0));
            }
            else
            {
                float dip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
                double x = rtl ? RenderSize.Width - layout.Value.Width : 0;
                var run = new GlyphRun(glyphs, 0, false, FontSize, dip, layout.Value.Indices, new Point(x, glyphs.Baseline * FontSize),
                    layout.Value.Advances, null, null, null, null, null, null);
                dc.DrawGlyphRun(Foreground, run);
            }

            if (rtl)
                dc.Pop();
        }
    }
}
