using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.ViewModels;

namespace CommandCenter.Pages.Tools
{
    // Add-ons in Settings: the view of AddonsViewModel. The before/after picture is the view's own: the two screenshots
    // with a divider the player drags while Control Bar Pro is chosen.
    public partial class AddonsSection : UserControl
    {
        // The pictures are 1920 x 1080 screenshots; the preview shows the bottom of the screen, where the bar is
        private const double CropTop = 700.0 / 1080;
        private const double MaxPreviewHeight = 300;
        private const double Corner = 4;

        private static readonly BitmapSource OriginalArt = Art("original");
        private static readonly BitmapSource ProArt = Art("pro");

        private bool _dragging;
        private double _split = 0.5;

        public AddonsSection()
        {
            InitializeComponent();
            Before.Source = OriginalArt;
            After.Source = ProArt;
            DataContextChanged += (_, e) =>
            {
                if (e.OldValue is INotifyPropertyChanged old)
                    old.PropertyChanged -= Model_PropertyChanged;
                if (e.NewValue is INotifyPropertyChanged model)
                    model.PropertyChanged += Model_PropertyChanged;
                ShowCompare();
            };
        }

        private AddonsViewModel? Model => DataContext as AddonsViewModel;

        private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e) => ShowCompare();

        private static BitmapSource Art(string name)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Assets/ControlBars/{name}.jpg");
            image.DecodePixelWidth = 1600;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            int top = (int)Math.Round(image.PixelHeight * CropTop);
            var crop = new CroppedBitmap(image, new Int32Rect(0, top, image.PixelWidth, image.PixelHeight - top));
            crop.Freeze();
            return crop;
        }

        // With Control Bar Pro chosen, the picture compares the two bars
        private void ShowCompare()
        {
            bool pro = Model?.ShowCompare == true;
            After.Visibility = Divider.Visibility = Handle.Visibility = AfterTag.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
            CompareBox.Cursor = pro ? Cursors.SizeWE : Cursors.Arrow;
            ApplySplit();
        }

        // The picture keeps the screenshots' shape: as wide as the panel, up to a height
        private void BarsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double width = BarsPanel.ActualWidth - 2;
            if (width <= 0 || !e.WidthChanged)
                return;
            double ratio = OriginalArt.PixelHeight / (double)OriginalArt.PixelWidth;
            double height = Math.Min(width * ratio, MaxPreviewHeight);
            CompareBox.Width = Math.Floor(height / ratio);
            CompareBox.Height = Math.Floor(height);
        }

        private void Compare_Down(object sender, MouseButtonEventArgs e)
        {
            if (Model?.ShowCompare != true)
                return;
            _dragging = true;
            CompareBox.CaptureMouse();
            Compare_Move(sender, e);
        }

        private void Compare_Move(object sender, MouseEventArgs e)
        {
            if (!_dragging)
                return;
            _split = Math.Clamp(e.GetPosition(CompareBox).X / Math.Max(1, CompareBox.ActualWidth), 0, 1);
            ApplySplit();
        }

        private void Compare_Up(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            CompareBox.ReleaseMouseCapture();
        }

        private void Compare_LostCapture(object sender, MouseEventArgs e) => _dragging = false;

        private void Compare_SizeChanged(object sender, SizeChangedEventArgs e) => ApplySplit();

        private void ApplySplit()
        {
            double w = CompareBox.ActualWidth, h = CompareBox.ActualHeight;
            if (w <= 0)
                return;
            double x = Math.Round(w * _split);
            After.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, w - x), h));
            CompareBox.Clip = new RectangleGeometry(new Rect(0, 0, w, h), Corner, Corner);
            Divider.Margin = new Thickness(x - 1, 0, 0, 0);
            Handle.Margin = new Thickness(x - Handle.Width / 2, 0, 0, 0);
        }
    }
}
