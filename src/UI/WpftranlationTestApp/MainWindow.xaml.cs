using NetAI.TestGenerator.Core.Services;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WpftranlationTestApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void TestButton_OnClick(object sender, RoutedEventArgs e)
        {
            //  await DownloadViaCloudFlare.DownloadWithCloudFlare();


            var builder = new AiPromptBuilder(@"C:\Users\steph\source\repos\WebObserver2\src\UI\WpfExplorer\aisettings.json");

            // Angenommen, du analysierst gerade einen "OrderController"
            string finalPrompt = await builder.BuildSystemPromptAsync();

            Console.WriteLine(finalPrompt);



            //bool ok = AiTranslatorRunner.Run(@"C:\Users\steph\source\repos\WebObserver2\WpfExplorer");

            var runner = new AiTranslatorRunner
            {
                ProjectDir = @"C:\Users\steph\source\repos\WebObserver2\src\UI\WpfExplorer",
                ApiKey = "sk-...",
                AppContext = "Rechnungs-Verwaltung für KMUs",
                //GlossaryPath = @"C:\Users\steph\source\repos\WebObserver2\WpfExplorer\glossary.json",
                SupportedLanguages = "en, de, it" // optional: überschreibt <SupportedLanguage> aus der .csproj
            };
            bool ok = await runner.Run(
                logInfo: msg => Console.WriteLine($"[Info]  {msg}"),
                logError: msg => Console.WriteLine($"[Fehler] {msg}"));

           

        }
    }
}