using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CommandCenter.Controls
{
    // A line icon on a 24 x 24 grid, drawn in the text colour: <c:Icon Kind="home" Width="17" Height="17"/>.
    // Pictures, not text, so a right-to-left layout keeps them as drawn.
    public sealed class Icon : Viewbox
    {
        private static readonly Dictionary<string, string> Shapes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = "M3,10.5 L12,3 L21,10.5 M5,9 V20.5 H10 V14.5 H14 V20.5 H19 V9",
            ["shield"] = "M12,2.8 L20,5.8 V12 C20,16.8 16.6,19.6 12,21.2 C7.4,19.6 4,16.8 4,12 V5.8 Z M8.8,12 L11,14.2 L15.4,9.8",
            ["map"] = "M3,6 L9,3 L15,6 L21,3 V18 L15,21 L9,18 L3,21 Z M9,3 V18 M15,6 V21",
            ["film"] = "M4,3 H20 A1,1 0 0 1 21,4 V20 A1,1 0 0 1 20,21 H4 A1,1 0 0 1 3,20 V4 A1,1 0 0 1 4,3 Z M7,3 V21 M17,3 V21 M3,7.5 H7 M3,12 H21 M3,16.5 H7 M17,7.5 H21 M17,16.5 H21",
            ["keyboard"] = "M4,5 H20 A2,2 0 0 1 22,7 V17 A2,2 0 0 1 20,19 H4 A2,2 0 0 1 2,17 V7 A2,2 0 0 1 4,5 Z M6,9 H6.01 M10,9 H10.01 M14,9 H14.01 M18,9 H18.01 M8,12.5 H8.01 M12,12.5 H12.01 M16,12.5 H16.01 M7.5,15.5 H16.5",
            ["layers"] = "M12,2.5 L22,7.2 L12,12 L2,7.2 Z M2,12.2 L12,17 L22,12.2 M2,16.8 L12,21.5 L22,16.8",
            ["sliders"] = "M21,4 H14 M10,4 H3 M21,12 H12 M8,12 H3 M21,20 H16 M12,20 H3 M14,2 V6 M8,10 V14 M16,18 V22",
            ["activity"] = "M22,12 H18 L15,21 L9,3 L6,12 H2",
            ["users"] = "M16,21 V19 A4,4 0 0 0 12,15 H6 A4,4 0 0 0 2,19 V21 M9,3 A4,4 0 1 1 8.99,3 Z M22,21 V19 A4,4 0 0 0 19,15.13 M16,3.13 A4,4 0 0 1 16,10.87",
            ["signal"] = "M2,20 H2.01 M7,20 V16 M12,20 V12 M17,20 V8",
            ["trophy"] = "M8,21 H16 M12,16.5 V21 M7,3.5 H17 V9 A5,5 0 0 1 7,9 Z M7,5.5 H4.5 V7.5 A3,3 0 0 0 7.2,10.6 M17,5.5 H19.5 V7.5 A3,3 0 0 1 16.8,10.6",
            ["server"] = "M4,2.5 H20 A2,2 0 0 1 22,4.5 V8.5 A2,2 0 0 1 20,10.5 H4 A2,2 0 0 1 2,8.5 V4.5 A2,2 0 0 1 4,2.5 Z M4,13.5 H20 A2,2 0 0 1 22,15.5 V19.5 A2,2 0 0 1 20,21.5 H4 A2,2 0 0 1 2,19.5 V15.5 A2,2 0 0 1 4,13.5 Z M6,6.5 H6.01 M6,17.5 H6.01",
            ["drive"] = "M22,12 H2 M5.5,5 L2,12 V18 A2,2 0 0 0 4,20 H20 A2,2 0 0 0 22,18 V12 L18.5,5 Z M6,16 H6.01 M10,16 H10.01",
            ["alert"] = "M12,3 L22,20 H2 Z M12,9.5 V13.5 M12,17 H12.01",
            ["check"] = "M20,6 L9,17 L4,12",
            ["check-circle"] = "M12,2 A10,10 0 1 1 11.99,2 Z M8.5,12 L11,14.5 L15.5,10",
            ["chevron"] = "M15,18 L9,12 L15,6",
            ["minus"] = "M5,12 H19",
            ["close"] = "M18,6 L6,18 M6,6 L18,18",
            ["refresh"] = "M3,12 A9,9 0 0 1 18.4,5.6 L21,8 M21,3 V8 H16 M21,12 A9,9 0 0 1 5.6,18.4 L3,16 M8,16 H3 V21",
            ["wrench"] = "M14.7,6.3 A1,1 0 0 0 14.7,7.7 L16.3,9.3 A1,1 0 0 0 17.7,9.3 L21.5,5.5 A6,6 0 0 1 13.5,13.5 L6.6,20.4 A2.1,2.1 0 0 1 3.6,17.4 L10.5,10.5 A6,6 0 0 1 18.5,2.5 Z",
            ["download"] = "M21,15 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V15 M7,10 L12,15 L17,10 M12,15 V3",
            ["folder"] = "M4,4 H9 L11,6.5 H20 A2,2 0 0 1 22,8.5 V18 A2,2 0 0 1 20,20 H4 A2,2 0 0 1 2,18 V6 A2,2 0 0 1 4,4 Z",
            ["info"] = "M12,2 A10,10 0 1 1 11.99,2 Z M12,16 V12 M12,8 H12.01",
            ["exit"] = "M9,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 H9 M16,17 L21,12 L16,7 M21,12 H9",
            ["tools"] = "M14.7,6.3 A1,1 0 0 0 14.7,7.7 L16.3,9.3 A1,1 0 0 0 17.7,9.3 L21.5,5.5 A6,6 0 0 1 13.5,13.5 L6.6,20.4 A2.1,2.1 0 0 1 3.6,17.4 L10.5,10.5 A6,6 0 0 1 18.5,2.5 Z",
        };

        // Drawn filled instead of outlined
        private static readonly Dictionary<string, string> Filled = new(StringComparer.OrdinalIgnoreCase)
        {
            ["play"] = "M7,4 L20,12 L7,20 Z",
        };

        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new PropertyMetadata("", (d, _) => ((Icon)d).Build()));

        public static readonly DependencyProperty ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner(typeof(Icon), new FrameworkPropertyMetadata(Brushes.White,
                FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((Icon)d).Build()));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Icon), new PropertyMetadata(1.8, (d, _) => ((Icon)d).Build()));

        public string Kind
        {
            get => (string)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public Icon()
        {
            Stretch = Stretch.Uniform;
            FlowDirection = FlowDirection.LeftToRight;
            SnapsToDevicePixels = true;
        }

        private void Build()
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            if (Filled.TryGetValue(Kind, out var fill))
                canvas.Children.Add(new Path { Data = Geometry.Parse(fill), Fill = Foreground, Stroke = Foreground, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round });
            else if (Shapes.TryGetValue(Kind, out var shape))
                canvas.Children.Add(new Path
                {
                    Data = Geometry.Parse(shape),
                    Stroke = Foreground,
                    StrokeThickness = StrokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                });
            Child = canvas;
        }
    }
}
