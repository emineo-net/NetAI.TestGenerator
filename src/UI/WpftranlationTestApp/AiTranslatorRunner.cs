using System.IO;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Config;

namespace WpftranlationTestApp;

/// <summary>
///     Eigenständiger Runner für AiTranslator.Core – einfach diese Datei in ein
///     beliebiges C#-Projekt kopieren (Voraussetzung: ProjectReference/PackageReference
///     auf AiTranslator.Core). Führt die .resx-Analyse/-Übersetzung aus, komplett ohne
///     MSBuild-Task-Infrastruktur.
///     Beispiel 1 – Einzeiler:
///     bool ok = AiTranslatorRunner.Run(@"C:\Users\steph\source\repos\WebObserver2\WpfExplorer");
///     Beispiel 2 – mit Instanz, eigenem Logging und weiteren Optionen:
///     var runner = new AiTranslatorRunner
///     {
///     ProjectDir = @"C:\Users\steph\source\repos\WebObserver2\WpfExplorer",
///     ApiKey = "sk-...",
///     AppContext = "Rechnungs-Verwaltung für KMUs",
///     GlossaryPath = @"C:\Users\steph\source\repos\WebObserver2\WpfExplorer\glossary.json",
///     SupportedLanguages = "en, de, it" // optional: überschreibt
///     <SupportedLanguage>
///         aus der .csproj
///         };
///         bool ok = runner.Run(
///         logInfo:  msg => Console.WriteLine($"[Info]  {msg}"),
///         logError: msg => Console.WriteLine($"[Fehler] {msg}"));
/// </summary>
public class AiTranslatorRunner
{
    /// <summary>Wurzelverzeichnis des Projekts, das nach .resx-Dateien durchsucht wird.</summary>
    public string ProjectDir { get; set; } = string.Empty;

    // Hinweis: Diese drei Properties spiegeln die Parameter des MSBuild-Tasks,
    // werden von ResxTranslationOrchestrator aktuell aber noch nicht ausgewertet
    // (reserviert für den späteren echten AiTranslationService).
    public string? ApiKey { get; set; }
    public string? AppContext { get; set; }
    public string? GlossaryPath { get; set; }

    /// <summary>
    ///     Optional: Zielsprachen als Kommaliste (z.B. "en, de, it"). Wenn gesetzt, überschreibt dieser Wert
    ///     das &lt;SupportedLanguage&gt;-Tag aus der .csproj - praktisch für Tests oder wenn keine .csproj
    ///     zur Verfügung steht bzw. gefunden wird.
    /// </summary>
    public string? SupportedLanguages { get; set; }

    /// <summary>
    ///     Führt die Analyse/Übersetzung aus.
    /// </summary>
    /// <param name="logInfo">Optionaler Callback für Info-Meldungen (Default: Console.WriteLine).</param>
    /// <param name="logError">Optionaler Callback für Fehlermeldungen (Default: Console.Error.WriteLine).</param>
    /// <returns>true bei Erfolg, false bei Fehler.</returns>
    public async Task<bool> Run(Action<string>? logInfo = null, Action<string>? logError = null)
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

        var orchestrator = new ResxTranslationOrchestrator();

        AiTestingConfig config;
        try
        {
            config = AiSettingsLoader.Load(ProjectDir);
        }
        catch (Exception ex)
        {
            return false;
        }

        var translator = config.Translator ?? new TranslatorConfig();
        var result = await orchestrator.ProcessProject(ProjectDir, translator); //, logInfo, supportedLanguagesOverride);

        if (!result.Success)
        {
            logError(result.ErrorMessage ?? "Unbekannter Fehler beim Ausführen des KI-Translators.");
            return false;
        }

        logInfo("✅ KI-Resx-Translator: Analyse abgeschlossen.");
        return true;
    }

    /// <summary>
    ///     Bequeme statische Kurzform für den Standardfall (nur ProjectDir).
    /// </summary>
    public static async Task<bool> Run(string projectDir, Action<string>? logInfo = null, Action<string>? logError = null)
    {
        return await new AiTranslatorRunner { ProjectDir = projectDir }.Run(logInfo, logError);
    }
}