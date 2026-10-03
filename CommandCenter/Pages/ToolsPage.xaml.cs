using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CommandCenter.Pages.Tools;
using CommandCenter.Services;

namespace CommandCenter.Pages
{
    // Hosts the tool sections (Health, Maps, Replays, Hotkeys, Add-ons); the window's top tabs pick one
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
            "maps" or "replays" or "hotkeys" or "addons" => section,
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
                    "addons" => new AddonsSection(),
                    _ => new HealthSection(),
                };
                _sections[id] = section;
            }
            bool switching = Current.Length > 0;
            Current = id;
            SectionHost.Content = section;
            if (section is IToolSection tool)
                tool.OnShown();

            // A section switched from another one fades in on its own (the page itself is already shown)
            if (switching && !App.HasArg("--capture"))
            {
                var move = new TranslateTransform(0, 10);
                section.RenderTransform = move;
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                section.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            }
        }
    }
}
