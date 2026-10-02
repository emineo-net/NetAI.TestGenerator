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

        var builder = new AiPromptBuilder(@"C:\Users\steph\source\repos\NetAI.TestGenerator\src\UI\WpftranlationTestApp\aisettings.json");

        // Angenommen, du analysierst gerade einen "OrderController"
        var finalPrompt = await builder.BuildSystemPromptAsync();

        var classCode = File.ReadAllText(@"C:\Users\steph\source\repos\NetAI.TestGenerator\src\UI\WpftranlationTestApp\MainWindow.xaml.cs");

        var fertigerPrompt = builder.GeneratePrompt(classCode, "ProcessOrder",
            " public async Task<string> AskAsync(string userMessage, string? systemMessage = null, CancellationToken ct = default)");

        Console.WriteLine(finalPrompt);

        var runner = new AiTranslatorRunner
        {
            ProjectDir = @"C:\Users\steph\source\repos\NetAI.TestGenerator\src\UI\WpftranlationTestApp",
            ApiKey = "sk-...",
            AppContext = "Rechnungs-Verwaltung für KMUs",
            SupportedLanguages = "en, de, it" // optional: überschreibt <SupportedLanguage> aus der .csproj
        };
        var ok = await runner.Run(fertigerPrompt, msg => Console.WriteLine($"[Info]  {msg}"), msg => Console.WriteLine($"[Fehler] {msg}"));
    }



    public static bool IsValidWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // 1. Instanziierungstest via .NET Uri-Klasse
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult))
        {
            return false;
        }

        // 2. Explizite Prüfung auf Web-Protokolle
        if (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // 3. Regex-Abgleich für das exakte Web-Format (verhindert z.B. "http://localhost")
        return true;
    }

}