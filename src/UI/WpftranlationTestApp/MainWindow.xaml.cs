using System.Windows;
using NetAI.TestGenerator.Core.Services;

namespace WpftranlationTestApp;

/// <summary>
///     Interaction logic for MainWindow.xaml
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
        var finalPrompt = await builder.BuildSystemPromptAsync();

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
        var ok = await runner.Run(msg => Console.WriteLine($"[Info]  {msg}"), msg => Console.WriteLine($"[Fehler] {msg}"));
    }
}