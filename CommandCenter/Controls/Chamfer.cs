using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CommandCenter.Controls
{
    // A plate with the top-right and bottom-left corners cut off, like the panels of the in-game control bar.
    // Draws its own fill and edge, clips the child to the same outline and can add two rivets.
    public class Chamfer : Decorator
    {
        public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
            nameof(Background), typeof(Brush), typeof(Chamfer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
            nameof(BorderBrush), typeof(Brush), typeof(Chamfer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
            nameof(BorderThickness), typeof(double), typeof(Chamfer), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
            nameof(Cut), typeof(double), typeof(Chamfer), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
        public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
            nameof(Padding), typeof(Thickness), typeof(Chamfer), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty RivetsProperty = DependencyProperty.Register(
            nameof(Rivets), typeof(bool), typeof(Chamfer), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty AccentEdgeProperty = DependencyProperty.Register(
            nameof(AccentEdge), typeof(Brush), typeof(Chamfer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
        public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
        public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
        public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }
        public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
        public bool Rivets { get => (bool)GetValue(RivetsProperty); set => SetValue(RivetsProperty, value); }

        // Optional 3px bar drawn along the left edge (used for selected and attention states)
        public Brush? AccentEdge { get => (Brush?)GetValue(AccentEdgeProperty); set => SetValue(AccentEdgeProperty, value); }

        private static readonly Brush RivetBrush = Freeze(new RadialGradientBrush(Color.FromRgb(0x8B, 0x92, 0x96), Color.FromRgb(0x2A, 0x2E, 0x31))
        {
            GradientOrigin = new Point(0.35, 0.35), Center = new Point(0.4, 0.4),
        });

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        public static Geometry Outline(Size size, double cut, double inset = 0)
        {
            double w = Math.Max(0, size.Width - inset * 2), h = Math.Max(0, size.Height - inset * 2);
            double c = Math.Max(0, Math.Min(cut - inset * 0.4, Math.Min(w, h) / 2));
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(inset, inset), true, true);
                ctx.LineTo(new Point(inset + w - c, inset), true, false);
                ctx.LineTo(new Point(inset + w, inset + c), true, false);
                ctx.LineTo(new Point(inset + w, inset + h), true, false);
                ctx.LineTo(new Point(inset + c, inset + h), true, false);
                ctx.LineTo(new Point(inset, inset + h - c), true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            var p = Padding;
            double b = BorderThickness;
            var inner = new Size(
                Math.Max(0, constraint.Width - p.Left - p.Right - b * 2),
                Math.Max(0, constraint.Height - p.Top - p.Bottom - b * 2));
            if (Child == null)
                return new Size(p.Left + p.Right + b * 2, p.Top + p.Bottom + b * 2);
            Child.Measure(inner);
            return new Size(Child.DesiredSize.Width + p.Left + p.Right + b * 2, Child.DesiredSize.Height + p.Top + p.Bottom + b * 2);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            if (Child != null)
            {
                var p = Padding;
                double b = BorderThickness;
                var rect = new Rect(p.Left + b, p.Top + b,
                    Math.Max(0, arrangeSize.Width - p.Left - p.Right - b * 2),
                    Math.Max(0, arrangeSize.Height - p.Top - p.Bottom - b * 2));
                Child.Arrange(rect);

                // Clip the child to the plate outline, expressed in the child's own coordinates
                var clip = Outline(arrangeSize, Cut, b).Clone();
                clip.Transform = new TranslateTransform(-rect.X, -rect.Y);
                clip.Freeze();
                if (Child is UIElement element)
                    element.Clip = clip;
            }
            return arrangeSize;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = RenderSize;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            // The edge is drawn as a ring around the fill, so see-through fills do not show the edge colour beneath
            double b = BorderThickness;
            var outer = Outline(size, Cut);
            bool hasBorder = BorderBrush != null && b > 0;
            var inner = hasBorder ? Outline(size, Cut, b) : outer;
            if (Background != null)
                dc.DrawGeometry(Background, null, inner);
            if (hasBorder)
                dc.DrawGeometry(BorderBrush, null, new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));

            if (AccentEdge != null)
                dc.DrawRectangle(AccentEdge, null, new Rect(b, b, 3, Math.Max(0, size.Height - b * 2 - Math.Max(0, Cut - b))));

            if (Rivets && size.Width > 40 && size.Height > 30)
            {
                dc.DrawEllipse(RivetBrush, null, new Point(13, 13), 3, 3);
                dc.DrawEllipse(RivetBrush, null, new Point(size.Width - 13, size.Height - 13), 3, 3);
            }
        }
    }
}
