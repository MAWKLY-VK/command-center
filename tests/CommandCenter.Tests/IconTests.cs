using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Controls;

namespace CommandCenter.Tests
{
    // The icons' line style (plan 3-c): square ends and mitre joins, so a dot is a small square. Rendered for real.
    public sealed class IconTests
    {
        // Draws the icon black on white at twice its grid size and returns the darkness (0 to 1) of one pixel
        private static double Darkness(string? kind, int x, int y)
        {
            double darkness = 0;
            Exception? error = null;
            var thread = new Thread(() =>
            {
              try
              {
                var icon = new Icon { Kind = kind!, Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0)), StrokeThickness = 2, Width = 48, Height = 48 };
                icon.Measure(new Size(48, 48));
                icon.Arrange(new Rect(0, 0, 48, 48));
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(255, 255, 255)), null, new Rect(0, 0, 48, 48));
                    dc.DrawRectangle(new VisualBrush(icon), null, new Rect(0, 0, 48, 48));
                }
                var bitmap = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                darkness = 1 - pixel[1] / 255.0;
              }
              catch (Exception ex)
              {
                  error = ex;
              }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null)
                throw new InvalidOperationException("Drawing the icon failed.", error);
            return darkness;
        }

        [Fact]
        public void A_dot_is_a_small_square_not_a_round_spot()
        {
            // "info" has a dot at (12, 8) on its 24 grid; at twice the size with a line of 2 units (4 px) a square end
            // covers (22..26, 14..18). The pixel at (25, 17) lies in that square's corner, outside a round end.
            Assert.True(Darkness("info", 23, 15) > 0.8, "The dot is missing.");
            Assert.True(Darkness("info", 25, 17) > 0.5, "The dot's corner is empty: its ends are round, not square.");
        }

        // A list item with no action binds no name (Health's issues); the page once stopped on it
        [Fact]
        public void An_icon_with_no_name_draws_nothing_instead_of_failing() =>
            Assert.Equal(0, Darkness(null, 24, 24));
    }
}
