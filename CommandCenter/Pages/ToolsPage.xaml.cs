using System.Windows;
using System.Windows.Controls;
using CommandCenter.Pages.Tools;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    public partial class ToolsPage : Page
    {
        private readonly Dictionary<string, UserControl> _sections = new();
        private string _current = "";

        public ToolsPage()
        {
            InitializeComponent();
            AppState.HealthChanged += () => Dispatcher.Invoke(ShowHealthBadge);
            ShowHealthBadge();
            TabHealth.IsChecked = true;
        }

        public bool HasPendingChanges => _sections.Values.OfType<IToolSection>().Any(s => s.HasPendingChanges);

        public void Select(string section)
        {
            var tab = section switch
            {
                "maps" => TabMaps,
                "replays" => TabReplays,
                "hotkeys" => TabHotkeys,
                "addons" => TabAddons,
                _ => TabHealth,
            };
            if (tab.IsChecked == true)
                Show(section);
            else
                tab.IsChecked = true;
        }

        public void OnShown()
        {
            if (SectionHost.Content is IToolSection section)
                section.OnShown();
        }

        public void ShowPart(string part)
        {
            if (SectionHost.Content is IToolSection section)
                section.ShowPart(part);
        }

        public Task ReadyAsync() => SectionHost.Content is IToolSection section ? section.ReadyAsync() : Task.CompletedTask;

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string id } && IsInitialized)
                Show(id);
        }

        private void Show(string id)
        {
            if (!_sections.TryGetValue(id, out var section))
            {
                section = id switch
                {
                    "maps" => new MapsSection(),
                    "replays" => new ReplaysSection(),
                    "hotkeys" => new HotkeysSection(),
                    "addons" => new AddonsSection(),
                    _ => new HealthSection(),
                };
                _sections[id] = section;
            }
            _current = id;
            SectionHost.Content = section;
            if (section is IToolSection tool)
                tool.OnShown();
        }

        private void ShowHealthBadge()
        {
            int count = AppState.ProblemCount + AppState.WarningCount;
            HealthBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HealthBadge.Background = AppState.ProblemCount > 0
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
            HealthBadgeText.Text = count.ToString();
        }

        private void Back_Click(object sender, RoutedEventArgs e) => ((MainWindow)Application.Current.MainWindow).ShowHome();
    }
}
