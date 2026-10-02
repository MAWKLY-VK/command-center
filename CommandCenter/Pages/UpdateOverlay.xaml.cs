using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommandCenter.Controls;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    // Shown over the whole window when a newer version exists. This copy stays blocked until the update is installed.
    public partial class UpdateOverlay : UserControl
    {
        private UpdateInfo? _update;
        private bool _busy;
        private bool _installed;

        public UpdateOverlay()
        {
            InitializeComponent();
        }

        public bool IsOpen => Visibility == Visibility.Visible;

        // Set once the new program has started and this one is closing
        public bool Restarting { get; private set; }

        public void Open(UpdateInfo update)
        {
            _update = update;
            Message.Text = $"Version {update.VersionText} is ready. This version stops working until you update.";
            Versions.Text = $"Installed {UpdateService.Current.ToString(3)} · New {update.Version.ToString(3)}";
            Notes.Text = update.Notes;
            NotesBox.Visibility = update.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Everything else in the window stops taking input
            if (Parent is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                {
                    if (child != this)
                        child.IsEnabled = false;
                }
            }
            Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Keyboard.Focus(UpdateNow));
        }

        // Enter starts the update, unless another button of the overlay has focus
        public void OnKey(KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !(Keyboard.FocusedElement is Button button && button != UpdateNow))
            {
                _ = InstallAsync();
                e.Handled = true;
            }
        }

        public async Task InstallAsync()
        {
            if (_update == null || _busy)
                return;
            _busy = true;
            UpdateNow.IsEnabled = false;
            ErrorBox.Visibility = Visibility.Collapsed;
            DownloadPage.Visibility = Visibility.Collapsed;
            ProgressBox.Visibility = Visibility.Visible;
            try
            {
                if (!_installed)
                {
                    ShowProgress((0, -1));
                    UpdateService.CheckFolder();
                    string file = await UpdateService.DownloadAsync(_update, new Progress<(long Done, long Total)>(ShowProgress));
                    Stage.Text = "Installing";
                    UpdateService.Install(file);
                    _installed = true;
                }
                Stage.Text = "Restarting";
                try
                {
                    UpdateService.Restart();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"The new version is installed but did not start ({ex.Message}). Close this window and start Command Center again.");
                }
                Restarting = true;
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                ProgressBox.Visibility = Visibility.Collapsed;
                Error.Text = "Update failed. " + ex.Message;
                ErrorBox.Visibility = Visibility.Visible;
                DownloadPage.Visibility = Visibility.Visible;
                UpdateNow.Content = "Try again";
                Ui.SetIcon(UpdateNow, Views.Icon("refresh"));
                UpdateNow.IsEnabled = true;
                Keyboard.Focus(UpdateNow);
            }
            finally
            {
                _busy = false;
            }
        }

        private void ShowProgress((long Done, long Total) p)
        {
            if (p.Total > 0)
            {
                Bar.Value = (double)p.Done / p.Total;
                Percent.Text = $"{p.Done * 100 / p.Total}%";
                Stage.Text = $"Downloading · {Views.Size(p.Done)} of {Views.Size(p.Total)}";
            }
            else
            {
                Bar.Value = 0;
                Percent.Text = "";
                Stage.Text = p.Done > 0 ? $"Downloading · {Views.Size(p.Done)}" : "Downloading";
            }
        }

        private void UpdateNow_Click(object sender, RoutedEventArgs e) => _ = InstallAsync();

        private void DownloadPage_Click(object sender, RoutedEventArgs e) => Views.Open(UpdateService.ReleasePage);

        private void Min_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is { } window)
                window.WindowState = WindowState.Minimized;
        }

        private void Max_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is { } window)
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
    }
}
