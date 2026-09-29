using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Task = Microsoft.Build.Utilities.Task;

namespace NetAI.TestGenerator.Tasks;

public class TestGeneratorTask : Task
{
    bool tääästXamlCs = true;
    bool tääästDebugger = false;
    [Required]
    public string ProjectDir { get; set; } = string.Empty;

    public string? CurrentConfiguration { get; set; }

    public bool IsPublishing { get; set; }

    // ---------------------------------------------------------------------
    //  Werden nur vom RunSemanticTestAnalysis-Target übergeben.
    //  Im RunResxTranslation-Target bleiben sie leer.
    // ---------------------------------------------------------------------
    public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] ReferencePaths { get; set; } = Array.Empty<ITaskItem>();
    public string? AnalysisOutputDirectory { get; set; }

    public override bool Execute()
    {


#if DEBUG
        if (tääästDebugger)
        {
            System.Diagnostics.Debugger.Launch();

        }
#endif


        // 1. WPF Protection: Wenn der Projektname auf "_wpftmp" endet, lautlos abbrechen
        if (!string.IsNullOrEmpty(ProjectDir) && ProjectDir.EndsWith("_wpftmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Modus-Erkennung:
        //    Wenn MSBuild uns Compile- und Reference-Items geliefert hat, sind
        //    wir im semantischen Analyse-Modus. Sonst: Resx-/Test-Generierung.
        var hasSemanticInputs =
            SourceFiles.Length > 0 &&
            ReferencePaths.Length > 0 &&
            !string.IsNullOrWhiteSpace(AnalysisOutputDirectory);

        return hasSemanticInputs
            ? ExecuteSemanticAnalysis()
            : ExecuteResxGeneration();
    }

    // =====================================================================
    //  Modus A: Semantische Analyse
    //  (aufgerufen aus RunSemanticTestAnalysis, NACH ResolveReferences)
    // =====================================================================
    private bool ExecuteSemanticAnalysis()
    {
        try
        {
            var sourcePaths = SourceFiles
                .Select(i => i.ItemSpec)
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .ToList();

            var referencePaths = ReferencePaths
                .Select(i => i.ItemSpec)
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .ToList();

            Log.LogMessage(MessageImportance.High,
                $"[NetAI] Semantische Analyse gestartet: " +
                $"{sourcePaths.Count} Quelldateien, {referencePaths.Count} Referenzen.");

            // --- Compilation aus Quelldateien + DLL-Pfaden bauen ---
            var assemblyName = Path.GetFileName(ProjectDir.TrimEnd('/', '\\'));
            var compilation = RoslynDllTestabilityAnalyzer.BuildCompilation(
                sourcePaths,
                referencePaths,
                assemblyName: string.IsNullOrEmpty(assemblyName) ? "TestabilityAnalysis" : assemblyName);

            var compileErrorCount = compilation.GetDiagnostics()
                .Count(d => d.Severity == DiagnosticSeverity.Error);

            if (compileErrorCount > 0)
            {
                Log.LogMessage(MessageImportance.High,
                    $"[NetAI] Hinweis: Compilation enthält {compileErrorCount} Fehler. " +
                    "Das ist normal, wenn Quelldateien anderer Projekte fehlen – " +
                    "die Analyse arbeitet mit dem, was auflösbar ist.");
            }

            // --- Analyse durchführen ---
            var analyzer = new RoslynDllTestabilityAnalyzer();
            var reports = new List<TestabilityReport>();

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var methods = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .ToList();

                foreach (var method in methods)
                {
                    try
                    {
                        var report = analyzer.AnalyzeFromCompilationAsync(
                                compilation,
                                method.Identifier.Text,
                                documentName: Path.GetFileName(tree.FilePath),
                                ct: CancellationToken.None)
                            .GetAwaiter().GetResult();

                        reports.Add(report);
                    }
                    catch (InvalidOperationException)
                    {
                        // Methode nicht gefunden / kein Methodensymbol – überspringen.
                    }
                }
            }

            // --- Report schreiben ---
            Directory.CreateDirectory(AnalysisOutputDirectory!);
            var reportPath = Path.Combine(AnalysisOutputDirectory!, "testability-report.txt");
            WriteAnalysisReport(reports, reportPath);

            var notTestableCount = reports.Count(r =>
                r.Verdict != null &&
                r.Verdict.StartsWith("NICHT", StringComparison.Ordinal));

            Log.LogMessage(MessageImportance.High,
                $"[NetAI] {reports.Count} Methoden analysiert, " +
                $"{notTestableCount} nicht direkt testbar. Report: {reportPath}");

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"[NetAI] Semantische Analyse fehlgeschlagen: {ex.Message}");
            return false;
        }
    }

    private static void WriteAnalysisReport(
        IReadOnlyList<TestabilityReport> reports,
        string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NetAI Testability Report");
        sb.AppendLine("========================");
        sb.AppendLine($"Erstellt: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Anzahl Methoden: {reports.Count}");
        sb.AppendLine();

        foreach (var group in reports
                     .GroupBy(r => r.DocumentName ?? "(unbenannt)")
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"# {group.Key}");
            sb.AppendLine();

            foreach (var report in group.OrderBy(r => r.Method?.Name, StringComparer.Ordinal))
            {
                var m = report.Method;
                sb.AppendLine($"  {m?.Signature ?? "(unbekannte Signatur)"}");
                sb.AppendLine($"    Accessibility : {m?.Accessibility}");
                sb.AppendLine($"    Static        : {m?.IsStatic}");
                sb.AppendLine($"    Async         : {m?.IsAsync}");
                sb.AppendLine($"    AsyncVoid     : {m?.IsAsyncVoid}");
                sb.AppendLine($"    Verdict       : {report.Verdict}");

                if (report.Recommendations?.Count > 0)
                {
                    sb.AppendLine("    Empfehlungen:");
                    foreach (var rec in report.Recommendations)
                        sb.AppendLine($"      - {rec}");
                }

                if (report.ReferencedTypes?.Count > 0)
                {
                    sb.AppendLine("    Referenzierte Typen:");
                    foreach (var t in report.ReferencedTypes)
                    {
                        sb.AppendLine(
                            $"      - {t.FullName} " +
                            $"(Kind: {t.Kind}, Mockable: {t.Mockable}, " +
                            $"Static: {t.UsedStatically})");
                    }
                }

                sb.AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    // =====================================================================
    //  Modus B: Resx-/Test-Generierung
    //  (aufgerufen aus RunResxTranslation, VOR PrepareForBuild)
    //  Unverändertes Verhalten zur bisherigen Version.
    // =====================================================================
    private bool ExecuteResxGeneration()
    {



        // Konfiguration aus aisettings.json laden
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

        // Sichere Modus-Prüfung (siehe ursprünglicher Kommentar)
        var config_ = (CurrentConfiguration ?? "Debug").ToLowerInvariant();

        Log.LogMessage(MessageImportance.High,
            "🤖 [NetAI] Modus-Bedingung erfüllt. Starte Test-Analyse...");

        // Test-Projekt-Verzeichnis ermitteln
        string testProjectDirectory =
            $"{ProjectDir.TrimEnd(Path.DirectorySeparatorChar)}.Tests";

        var collectedIssues = new List<string>();
        var orchestrator = new ResxTranslationOrchestrator();

        var csharpFiles = new List<string>();

        if (tääästXamlCs)
        {
            csharpFiles = Directory.GetFiles(ProjectDir, "*.cs", SearchOption.AllDirectories)
                .Select(file => Path.GetFullPath(file))
                .Where(file =>
                    !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&

                    !file.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) &&

                    !file.Contains("_wpftmp") &&

                    !file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) &&
                    !file.Contains("Designer.cs") &&


                    !file.Contains("AiTranslatorRunner") &&

                    !file.Contains("AssemblyAttributes") &&
                    !file.Contains("AssemblyInfo")
                ).ToList();
        }
        else
        {
            csharpFiles = Directory.GetFiles(ProjectDir, "*.cs", SearchOption.AllDirectories)
                .Select(file => Path.GetFullPath(file))
                .Where(file =>
                    !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&

                    !file.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) &&

                    !file.Contains("_wpftmp") &&

                    !file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) &&
                    !file.Contains("Designer.cs") &&

                    !file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) &&

                    !file.Contains("AiTranslatorRunner") &&

                    !file.Contains("AssemblyAttributes") &&
                    !file.Contains("AssemblyInfo")
                ).ToList();
        }

        bool overallSuccess = true;

        Log.LogMessage(MessageImportance.High, $"🤖 [NetAI]csharpFiles  {csharpFiles.Count()} ");


        foreach (var sourceFilePath in csharpFiles)
        {
            try
            {

                Log.LogMessage(MessageImportance.High, "🤖 [NetAI]orchestrator.ProcessProjectAsyn start...");
                string result = System.Threading.Tasks.Task.Run(async () =>
                    await orchestrator.ProcessProjectAsync(sourceFilePath, testProjectDirectory, message =>
                    {
                        if (string.IsNullOrWhiteSpace(message)) return;

                        var isError = message.Contains("[NetAI Error]") || message.Contains("Error:");
                        var isWarning = message.Contains("[NetAI Warning]") || message.Contains("Warning:");

                        if (isError)
                        {
                            var cleanMessage = message
                                .Replace("[NetAI Error]", "")
                                .Replace("Error:", "")
                                .Trim();
                            Log.LogError($"[NetAI] {cleanMessage}");
                            collectedIssues.Add($"[ERROR] {cleanMessage}");
                        }
                        else if (isWarning)
                        {
                            var cleanMessage = message
                                .Replace("[NetAI Warning]", "")
                                .Replace("Warning:", "")
                                .Trim();
                            Log.LogWarning($"[NetAI] {cleanMessage}");
                            collectedIssues.Add($"[WARNING] {cleanMessage}");
                        }
                        else
                        {
                            Log.LogMessage(MessageImportance.High, message);
                        }
                    })
                ).GetAwaiter().GetResult();

                Log.LogMessage(MessageImportance.High, "🤖 [NetAI]orchestrator.ProcessProjectAsyn end...");

                Log.LogMessage(MessageImportance.High, "🤖 [NetAI] Modus-Bedingung erfüllt. Starte Test-Analyse...");

                if (result != "ok")
                {
                    Log.LogError($"[NetAI] Fehler bei der Verarbeitung von " +
                                 $"'{Path.GetFileName(sourceFilePath)}': {result}");
                    collectedIssues.Add($"[ERROR] {result}");
                    overallSuccess = false;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[NetAI] Kritischer Fehler bei Datei " +
                             $"'{Path.GetFileName(sourceFilePath)}': {ex.Message}");
                collectedIssues.Add($"[CRITICAL] {ex.Message}");
                overallSuccess = false;
            }
        }

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