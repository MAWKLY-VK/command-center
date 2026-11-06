using System.Windows;
using System.Windows.Controls;

namespace CommandCenter.Pages
{
    // Help: the common questions (moved from Health, their buttons unchanged), the support report and where to ask
    public partial class HelpPage : Page
    {
        // Generals Online's own Discord address, the one its website links to
        private const string Discord = "https://discord.playgenerals.online";

        private const string Issues = "https://github.com/MAWKLY-VK/command-center/issues";

        public HelpPage()
        {
            InitializeComponent();
            // Built each time Help shows, so the order follows whether the game is found
            Loaded += (_, _) => QuestionList.ItemsSource = CommonQuestions.All(Views.Main.RunChecks);
        }

        private void Action_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: QuestionAction action })
                action.Run();
        }

        private void Report_Click(object sender, RoutedEventArgs e) => Views.CopySupportReport();

        private void Discord_Click(object sender, RoutedEventArgs e) => Views.Open(Discord);

        private void Issues_Click(object sender, RoutedEventArgs e) => Views.Open(Issues);
    }
}
