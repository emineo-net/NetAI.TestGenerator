using System.IO;
using NetAI.TestGenerator.Core;
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

        var result = await orchestrator.ProcessProject(ProjectDir, prompt); //, translator); //, logInfo, supportedLanguagesOverride);

        if (result.Contains("error"))
        {
            logError(result);
            return false;
        }

        logInfo("✅ KI-Resx-Translator: Analyse abgeschlossen.");
        return true;
    }

}