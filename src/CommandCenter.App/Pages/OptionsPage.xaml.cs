using CommandCenter.Imaging;
using CommandCenter.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace CommandCenter.Pages
{
    // Settings: Command Center's own sections and Generals Online's, on SettingsViewModel. The page builds the view models
    // and opens sections; leaving it (Save and go back, a tab, or closing the window) writes what changed.
    public partial class OptionsPage : Page
    {
        private readonly SettingsViewModel _settings;

        // A control bar change chosen in Add-ons and not applied yet
        public bool HasPendingChanges => _settings.Addons.HasPendingChanges;

        public OptionsPage()
        {
            InitializeComponent();
            var shell = Views.Main;
            var game = new GameSettingsViewModel(shell, GameLauncher.IsGameRunning, HealthService.Display.Modes, PatchChanged);
            _settings = new SettingsViewModel(
                new GameFolderViewModel(AppServices.GameFolder, shell, AppState.ClientVersion, () => GamePaths.UserData),
                new LauncherViewModel(shell, AppState.Version),
                new AppearanceViewModel(AppServices.Appearance),
                new AddonsViewModel(AppServices.Addons, AppServices.GameFolder, shell),
                game,
                // The diagnostics keep their own direct route: the game does not go through a proxy either
                new NetworkDiagnosticsViewModel(token => new NetworkDiagnosticsService().RunAsync(token), Copy),
                new PluginsViewModel(game, () => GamePaths.Game));
            DataContext = _settings;
            // Another game folder: its settings and its control bar are read again
            AppServices.GameFolder.Changed += () =>
            {
                _settings.Load();
                _settings.Addons.Refresh();
            };
            // Read each time Settings opens, so changes made by the game or the official launcher show
            Loaded += (_, _) => _settings.Load();
        }

        // Opens a section by its sidebar name, e.g. "network" or "launcher" (used when taking screenshots)
        public void ShowPart(string part)
        {
            var tab = Sections().Select(s => s.Tab)
                .FirstOrDefault(r => r.Name.Equals("rb" + part.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (tab != null)
                tab.IsChecked = true;
        }

        // Used by --capture with "settings:<section>:end", for a section taller than the window
        public void ScrollToEnd()
        {
            SectionScroller.UpdateLayout();
            SectionScroller.ScrollToEnd();
        }

        // Also used by --capture with "settings:network:run"
        public Task RunDiagnosticsAsync() => _settings.Diagnostics.RunCommand.ExecuteAsync(null);

        // Saves what was set in Generals Online's sections; the window calls it when Settings is left by a tab or closed
        public void Commit()
        {
            if (IsLoaded)
                _settings.Commit();
        }

        private (RadioButton Tab, FrameworkElement Panel)[] Sections() =>
        [
            (rbGame, PanelGame), (rbAppearance, PanelAppearance), (rbAddons, PanelAddonsHost), (rbCamera, PanelCamera),
            (rbChat, PanelChat), (rbInput, PanelInput), (rbGraphics, PanelGraphics), (rbSocial, PanelSocial),
            (rbNetwork, PanelNetwork), (rbDataPacks, PanelDataPacks), (rbPlugins, PanelPlugins), (rbLauncher, PanelLauncher),
        ];

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelLauncher == null)
                return; // the first tab is checked while the page is still being built

            foreach (var (tab, panel) in Sections())
                panel.Visibility = ReferenceEquals(tab, sender) ? Visibility.Visible : Visibility.Collapsed;

            if (ReferenceEquals(sender, rbPlugins))
                _settings.Plugins.Refresh();
            else if (ReferenceEquals(sender, rbAddons))
            {
                PanelAddons.Content ??= new Tools.AddonsSection { DataContext = _settings.Addons };
                _settings.Addons.Refresh();
            }
        }

        // Leaving Settings saves, so this button only goes back
        private void BtnBack_Click(object sender, RoutedEventArgs e) => Views.Main.ShowHome();

        // The hotkey letters on the pictures follow the buttons the game will show
        private static void PatchChanged()
        {
            AppState.ForgetHotkeys();
            if (AppSettings.Current.IconLetters)
                _ = Task.Run(async () =>
                {
                    if (await AppState.Hotkeys() is { } hotkeys)
                        IconLettersService.RefreshIfEnabled(hotkeys);
                });
        }

        private static void Copy(string report)
        {
            try
            {
                Clipboard.SetText(report);
                Views.Main.Toast("Diagnostics copied, with your addresses hidden. Paste them in the Generals Online Discord.");
            }
            catch (Exception ex)
            {
                Views.Main.Toast(string.Format("Could not copy: {0}", ex.Message), isError: true);
            }
        }
    }
}
