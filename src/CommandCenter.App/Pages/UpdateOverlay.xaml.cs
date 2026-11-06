using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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

        // The window hides what is under the screen when it opens
        public event Action? Opened;

        public void Open(UpdateInfo update)
        {
            _update = update;
            ShowReady();
            Message.Text = string.Format("Version {0} is ready. This version stops working until you update.", update.VersionText);
            Versions.Text = string.Format("Installed {0}, new {1}", UpdateService.Current.ToString(3), update.Version.ToString(3));
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
            Opened?.Invoke();
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
            ShowWorking();
            try
            {
                if (!_installed)
                {
                    ShowProgress((0, -1));
                    bool asAdministrator = UpdateService.FolderNeedsAdministrator();
                    string file = await UpdateService.DownloadAsync(_update, new Progress<(long Done, long Total)>(ShowProgress));
                    Stage.Text = asAdministrator ? "Installing. Allow it when Windows asks for permission." : "Installing";
                    await UpdateService.InstallAsync(file, _update, asAdministrator);
                    _installed = true;
                }
                Stage.Text = "Restarting";
                try
                {
                    await UpdateService.RestartAsync(_update.Version);
                }
                catch (PreviousVersionRestoredException)
                {
                    // This version runs again in its place, so Try again downloads and installs again
                    _installed = false;
                    throw;
                }
                Restarting = true;
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                ShowFailed(ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        // For review screenshots: the screen for a made-up next version (update), during its download (update:progress),
        // or after Windows was refused the permission it asked for (update:error)
        public void Preview(string state)
        {
            var next = new Version(UpdateService.Current.Major, UpdateService.Current.Minor + 1, 0);
            Open(new UpdateInfo(next, next.ToString(3), "", "",
                "A new look for Home, Settings, Health and Help, with the game's own art behind every page. Interface sounds from the game, off by default."));
            if (state == "progress")
            {
                ShowWorking();
                ShowProgress((1_800_000, 4_400_000));
            }
            else if (state == "error")
            {
                ShowFailed("Windows asked for permission, and it was not given.");
            }
        }

        // As it opens: the button to update, nothing in progress, no error
        private void ShowReady()
        {
            ProgressBox.Visibility = Visibility.Collapsed;
            Error.Visibility = Visibility.Collapsed;
            DownloadPage.Visibility = Visibility.Collapsed;
            UpdateNowLabel.Text = "Update now";
            UpdateNow.IsEnabled = true;
        }

        private void ShowWorking()
        {
            UpdateNow.IsEnabled = false;
            Error.Visibility = Visibility.Collapsed;
            DownloadPage.Visibility = Visibility.Collapsed;
            ProgressBox.Visibility = Visibility.Visible;
        }

        private void ShowFailed(string reason)
        {
            ProgressBox.Visibility = Visibility.Collapsed;
            Error.Text = string.Format("Update failed. {0} Try again, or get the new version from the download page.", reason);
            Error.Visibility = Visibility.Visible;
            DownloadPage.Visibility = Visibility.Visible;
            UpdateNowLabel.Text = "Try again";
            UpdateNow.IsEnabled = true;
            Keyboard.Focus(UpdateNow);
        }

        private void ShowProgress((long Done, long Total) p)
        {
            if (p.Total > 0)
            {
                Bar.Value = (double)p.Done / p.Total;
                Percent.Text = $"{p.Done * 100 / p.Total}%";
                Stage.Text = string.Format("Downloading {0} of {1}", Views.Size(p.Done), Views.Size(p.Total));
            }
            else
            {
                Bar.Value = 0;
                Percent.Text = "";
                Stage.Text = p.Done > 0 ? string.Format("Downloading, {0} so far", Views.Size(p.Done)) : "Downloading";
            }
        }

        private void UpdateNow_Click(object sender, RoutedEventArgs e) => _ = InstallAsync();

        private void DownloadPage_Click(object sender, RoutedEventArgs e) => Views.Open(UpdateService.ReleasePage);

        private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
    }
}
