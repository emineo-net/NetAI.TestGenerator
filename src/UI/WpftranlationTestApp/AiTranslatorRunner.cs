using System.IO;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Config;

namespace WpftranlationTestApp;

public class AiTranslatorRunner
{
    public string ProjectDir { get; set; } = string.Empty;

    public string? ApiKey { get; set; }
    public string? AppContext { get; set; }

    public string? SupportedLanguages { get; set; }

    public async Task<bool> Run(string prompt, Action<string>? logInfo = null, Action<string>? logError = null)
    {
        logInfo ??= message => Console.WriteLine(message);
        logError ??= message => Console.Error.WriteLine(message);

        if (string.IsNullOrWhiteSpace(ProjectDir) || !Directory.Exists(ProjectDir))
        {
            logError($"Ungültiges oder nicht existierendes ProjectDir: '{ProjectDir}'");
            return false;
        }

        logInfo($"🤖 KI-Resx-Translator: Starte Analyse in '{ProjectDir}'...");

        var supportedLanguagesOverride = string.IsNullOrWhiteSpace(SupportedLanguages)
            ? null
            : SupportedLanguages.Split(',').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        AiTestingConfig config;
        try
        {
            config = AiSettingsLoader.Load(ProjectDir);
        }
        catch (Exception ex)
        {
            logError($"aisettings.json konnte nicht geladen werden: {ex.Message}");
            return false;
        }

        ResxTranslationOrchestrator orchestrator;
        try
        {
            orchestrator = new ResxTranslationOrchestrator(config);
        }
        catch (ArgumentException ex)
        {
            logError($"Ungültige Framework-Einstellungen in aisettings.json: {ex.Message}");
            return false;
        }

        var projectDir = ProjectDir.TrimEnd(Path.DirectorySeparatorChar);

        // 2. Den reinen Projektnamen extrahieren (z. B. "WpftranlationTestApp")
        var projectName = Path.GetFileName(projectDir);

        // 3. Generisch das Solution-Root-Verzeichnis finden
        var solutionDir = FindSolutionRoot(projectDir);

        if (solutionDir != null)
        {
            // 4. Den Zielpfad immer fix unter "tests\UnitTests\" zusammenbauen
            var testProjectDirectory = Path.Combine(solutionDir, "tests", "UnitTests", $"{projectName}.Tests");

            // Ergebnis: C:\Users\steph\source\repos\NetAI.TestGenerator\tests\UnitTests\WpftranlationTestApp.Tests

            var sourceFilePath = Path.Combine(ProjectDir, "MainWindow.xaml.cs");
            var sourceFiles = Directory.GetFiles(ProjectDir, "*.cs", SearchOption.AllDirectories).Where(file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();
            var referencePaths = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location)).Select(assembly => assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var compilation = RoslynDllTestabilityAnalyzer.BuildCompilation(sourceFiles, referencePaths, projectName);

            var result = await orchestrator.ProcessProjectAsync(sourceFilePath, testProjectDirectory, logInfo, compilation);

            if (result.Contains("error"))
            {
                logError(result);
                return false;
            }

            logInfo("✅ KI-Resx-Translator: Analyse abgeschlossen.");
            return true;
        }

        // Fallback, falls keine .sln-Datei gefunden wurde
        throw new DirectoryNotFoundException("Solution-Verzeichnis konnte nicht ermittelt werden.");
    }

    private static string FindSolutionRoot(string currentDir)
    {
        var directory = new DirectoryInfo(currentDir);

        while (directory != null)
        {
            // Holt alle Dateien und prüft, ob eine davon auf .sln oder .slnx endet
            var hasSolutionFile = directory.GetFiles().Any(f =>
                f.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                f.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase));

            if (hasSolutionFile)
            {
                return directory.FullName;
            }

            // Eine Ebene nach oben gehen
            directory = directory.Parent;
        }

        return null;
    }
}