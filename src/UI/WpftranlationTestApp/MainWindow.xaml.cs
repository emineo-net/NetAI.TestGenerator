using System.IO;
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

        var classCode = File.ReadAllText(@"C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Core\LocalLlmClient.cs");

        var fertigerPrompt = builder.GeneratePrompt(
            klassenCode: classCode,
            methodenName: "ProcessOrder",
            methodenSignatur: " public async Task<string> AskAsync(string userMessage, string? systemMessage = null, CancellationToken ct = default)"
        );

        Console.WriteLine(finalPrompt);

        //bool ok = AiTranslatorRunner.Run(@"C:\Users\steph\source\repos\WebObserver2\WpfExplorer");

        var runner = new AiTranslatorRunner
        {
            ProjectDir = @"C:\Users\steph\source\repos\WebObserver2\src\UI\WpfExplorer",
            ApiKey = "sk-...",
            AppContext = "Rechnungs-Verwaltung für KMUs",
            SupportedLanguages = "en, de, it" // optional: überschreibt <SupportedLanguage> aus der .csproj
        };
        var ok = await runner.Run(fertigerPrompt,msg => Console.WriteLine($"[Info]  {msg}"), msg => Console.WriteLine($"[Fehler] {msg}"));
    }
}