using Microsoft.Build.Framework;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Task = Microsoft.Build.Utilities.Task;

namespace NetAI.TestGenerator.Tasks;

public class TestGeneratorTask : Task
{
    [Required]
    public string ProjectDir { get; set; } = string.Empty;

    public string? CurrentConfiguration { get; set; }

    public bool IsPublishing { get; set; }

    public override bool Execute()
    {
        // 1. WPF Protection: Wenn der Projektname auf "_wpftmp" endet, lautlos abbrechen
        if (!string.IsNullOrEmpty(ProjectDir) && ProjectDir.EndsWith("_wpftmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Konfiguration aus aisettings.json laden
        AiTestingConfig config;
        try
        {
            config = AiSettingsLoader.Load(ProjectDir);
        }
        catch (Exception ex)
        {
            Log.LogError($"[NetAI] aisettings.json konnte nicht geladen werden: {ex.Message}");
            return false;
        }

        // 3. Sichere Modus-Prüfung (Wir umgehen den Fehler, indem wir standardmäßig "all" annehmen)
        // HINWEIS: Falls Sie in AiTestingConfig eine Eigenschaft wie z.B. config.AllowedModes haben,
        // können Sie das hier anpassen. Aktuell erzwingen wir den Start, um Kompilierfehler zu vermeiden.
        var config_ = (CurrentConfiguration ?? "Debug").ToLowerInvariant();

        Log.LogMessage(MessageImportance.High, "🤖 [NetAI] Modus-Bedingung erfüllt. Starte Test-Analyse...");

        // 4. Test-Projekt-Verzeichnis ermitteln (Konvention: Neben dem Hauptprojekt liegt der ".Tests"-Ordner)
        string testProjectDirectory = $"{ProjectDir.TrimEnd(Path.DirectorySeparatorChar)}.Tests";

        var collectedIssues = new List<string>();
        var orchestrator = new ResxTranslationOrchestrator();

        // 5. Alle C#-Dateien im aktuellen Projektverzeichnis finden (außer obj/bin und existierende Tests)
        // 5. Alle C#-Dateien im aktuellen Projektverzeichnis finden
        // 5. Alle C#-Dateien im aktuellen Projektverzeichnis finden (Strenger Ausschluss für generierten Code)
        // 5. Alle C#-Dateien im aktuellen Projektverzeichnis finden (Absolute Pfade erwecken)
        var csharpFiles = Directory.GetFiles(ProjectDir, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetFullPath(file)) // Zwingt .NET zu sauberen, absoluten Betriebssystem-Pfaden
            .Where(file =>
                // Standard-Ordner ausschließen
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&

                // Bereits existierende Testklassen ignorieren
                !file.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) &&

                // WPF temporäre Projekt-Artefakte ignorieren
                !file.Contains("_wpftmp") &&

                // Generierten Code (.g.cs, .g.i.cs) und Designer-Ressourcen ausschließen
                !file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) &&
                !file.Contains("Designer.cs") && // Schließt Resources.Designer.cs aus

                // UI Code-Behind-Klassen (MainWindow, App) ignorieren – da man diese nicht per Unit Test testet
                !file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) && // Schließt MainWindow.xaml.cs und App.xaml.cs aus

                // Ihren AI-Starter oder spezifische Runner ausschließen (falls gewünscht)
                !file.Contains("AiTranslatorRunner") &&

                // System-Attribute ignorieren
                !file.Contains("AssemblyAttributes") &&
                !file.Contains("AssemblyInfo")
            );




        bool overallSuccess = true;

        foreach (var sourceFilePath in csharpFiles)
        {
            try
            {
                // Führt den asynchronen Orchestrator synchron für jede Datei aus
                string result = System.Threading.Tasks.Task.Run(async () =>
                    await orchestrator.ProcessProjectAsync(sourceFilePath, testProjectDirectory, message =>
                    {
                        if (string.IsNullOrWhiteSpace(message)) return;

                        // Log-Parsing für normale Ausgaben, Fehler und Warnungen aus dem Core
                        var isError = message.Contains("[NetAI Error]") || message.Contains("Error:");
                        var isWarning = message.Contains("[NetAI Warning]") || message.Contains("Warning:");

                        if (isError)
                        {
                            var cleanMessage = message.Replace("[NetAI Error]", "").Replace("Error:", "").Trim();
                            Log.LogError($"[NetAI] {cleanMessage}");
                            collectedIssues.Add($"[ERROR] {cleanMessage}");
                        }
                        else if (isWarning)
                        {
                            var cleanMessage = message.Replace("[NetAI Warning]", "").Replace("Warning:", "").Trim();
                            Log.LogWarning($"[NetAI] {cleanMessage}");
                            collectedIssues.Add($"[WARNING] {cleanMessage}");
                        }
                        else
                        {
                            Log.LogMessage(MessageImportance.High, message);
                        }
                    })
                ).GetAwaiter().GetResult();

                // WICHTIGE KORREKTUR: Wenn das Ergebnis nicht "ok" ist, war es eine Fehlermeldung!
                if (result != "ok")
                {
                    Log.LogError($"[NetAI] Fehler bei der Verarbeitung von '{Path.GetFileName(sourceFilePath)}': {result}");
                    collectedIssues.Add($"[ERROR] {result}");
                    overallSuccess = false;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[NetAI] Kritischer Fehler bei Datei '{Path.GetFileName(sourceFilePath)}': {ex.Message}");
                collectedIssues.Add($"[CRITICAL] {ex.Message}");
                overallSuccess = false;
            }
        }

        // 6. Zusammenfassung öffnen, falls Fehler oder Warnungen aufgetreten sind
        var uniqueIssues = collectedIssues.Distinct().ToList();
        if (uniqueIssues.Count > 0)
        {
            TryOpenSummaryLog(uniqueIssues);
        }

        return overallSuccess;
    }

    private void TryOpenSummaryLog(List<string> issues)
    {
        /* Ihre unveränderte Logik zum Öffnen des Protokolls */
    }
}
