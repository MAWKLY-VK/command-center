using System.Windows.Controls;
using CommandCenter.Pages.Tools;

namespace CommandCenter.Pages
{
    // Hosts the sections Replays, Maps, Hotkeys and Health; the window's tabs pick one
    public partial class ToolsPage : Page
    {
        private readonly Dictionary<string, UserControl> _sections = new();

        public ToolsPage()
        {
            InitializeComponent();
            Show("health");
        }

        public string Current { get; private set; } = "";

        public bool HasPendingChanges => _sections.Values.OfType<IToolSection>().Any(s => s.HasPendingChanges);

        public void Select(string section) => Show(section switch
        {
            "maps" or "replays" or "hotkeys" => section,
            _ => "health",
        });

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

        // For --capture: the end of a long section (Health only so far)
        public void ScrollToEnd()
        {
            if (SectionHost.Content is HealthSection health)
                health.ScrollToEnd();
        }

        // Health's checks again, with its own button showing that they run
        public void RunHealthChecks()
        {
            Show("health");
            if (SectionHost.Content is HealthSection health)
                _ = health.RunChecksAsync();
        }

        private void Show(string id)
        {
            if (id == Current)
                return;
            if (!_sections.TryGetValue(id, out var section))
            {
                section = id switch
                {
                    "maps" => new MapsSection(),
                    "replays" => new ReplaysSection(),
                    "hotkeys" => new HotkeysSection(),
                    _ => new HealthSection(),
                };
                _sections[id] = section;
            }
            Current = id;
            SectionHost.Content = section;
            if (section is IToolSection tool)
                tool.OnShown();
        }
    }
}
