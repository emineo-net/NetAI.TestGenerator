using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Config;
using System.Text;
using Task = Microsoft.Build.Utilities.Task;

namespace NetAI.TestGenerator.Tasks;

public class TestGeneratorTask : Task
{
    bool testXamlCs = true;
    bool testDebugger = false;
    [Required]
    public string ProjectDir { get; set; } = string.Empty;

    public string? CurrentConfiguration { get; set; }

    public bool IsPublishing { get; set; }
    public bool PromptOnly { get; set; }

    public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] ReferencePaths { get; set; } = Array.Empty<ITaskItem>();
    public string? AnalysisOutputDirectory { get; set; }

    public override bool Execute()
    {


//#if DEBUG
//        if (testDebugger)
//        {
//            System.Diagnostics.Debugger.Launch();

//        }
//#endif

        var folderName = Path.GetFileName(ProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (!string.IsNullOrEmpty(folderName) && folderName.EndsWith("_wpftmp", StringComparison.OrdinalIgnoreCase))
        {
            return true; 
        }


        var hasSemanticInputs =
            SourceFiles.Length > 0 &&
            ReferencePaths.Length > 0 &&
            !string.IsNullOrWhiteSpace(AnalysisOutputDirectory);

        return hasSemanticInputs
            ? ExecuteSemanticAnalysis()
            : ExecuteResxGeneration();
    }

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
                $"[NetAI] Semantic analysis started: " +
                $"{sourcePaths.Count} source files, {referencePaths.Count} references.");

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
                    $"[NetAI] Note: Compilation contains {compileErrorCount} errors. " +
                    "This is normal when source files from other projects are missing; " +
                    "the analysis uses whatever can be resolved.");
            }

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
                        var report = System.Threading.Tasks.Task.Run(async () =>
                            await analyzer.AnalyzeFromCompilationAsync(
                                compilation,
                                method.Identifier.Text,
                                documentName: Path.GetFileName(tree.FilePath),
                                ct: CancellationToken.None).ConfigureAwait(false)
                        ).GetAwaiter().GetResult();

                        reports.Add(report);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

            }

            Directory.CreateDirectory(AnalysisOutputDirectory!);
            var reportPath = Path.Combine(AnalysisOutputDirectory!, "testability-report.txt");
            WriteAnalysisReport(reports, reportPath);

            var notTestableCount = reports.Count(r =>
                r.Verdict != null &&
                r.Verdict.StartsWith("NOT", StringComparison.Ordinal));

            Log.LogMessage(MessageImportance.High,
                $"[NetAI] Analyzed {reports.Count} methods; " +
                $"{notTestableCount} are not directly testable. Report: {reportPath}");

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"[NetAI] Semantic analysis failed: {ex.Message}");
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
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Method count: {reports.Count}");
        sb.AppendLine();

        foreach (var group in reports
                     .GroupBy(r => r.DocumentName ?? "(unnamed)")
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"# {group.Key}");
            sb.AppendLine();

            foreach (var report in group.OrderBy(r => r.Method?.Name, StringComparer.Ordinal))
            {
                var m = report.Method;
                sb.AppendLine($"  {m?.Signature ?? "(unknown signature)"}");
                sb.AppendLine($"    Accessibility : {m?.Accessibility}");
                sb.AppendLine($"    Static        : {m?.IsStatic}");
                sb.AppendLine($"    Async         : {m?.IsAsync}");
                sb.AppendLine($"    AsyncVoid     : {m?.IsAsyncVoid}");
                sb.AppendLine($"    Verdict       : {report.Verdict}");

                if (report.Recommendations?.Count > 0)
                {
                    sb.AppendLine("    Recommendations:");
                    foreach (var rec in report.Recommendations)
                        sb.AppendLine($"      - {rec}");
                }

                if (report.ReferencedTypes?.Count > 0)
                {
                    sb.AppendLine("    Referenced types:");
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

    private bool ExecuteResxGeneration()
    {

        AiTestingConfig config;
        try
        {
            config = AiSettingsLoader.Load(ProjectDir);
        }
        catch (Exception ex)
        {
            Log.LogError($"[NetAI] Could not load aisettings.json: {ex.Message}");
            return false;
        }

        var config_ = (CurrentConfiguration ?? "Debug").ToLowerInvariant();

        Log.LogMessage(MessageImportance.High,
            "[NetAI] Mode condition met. Starting test analysis...");

        string? solutionDirectory = FindSolutionDirectory(ProjectDir);
        if (solutionDirectory is null)
        {
            Log.LogError($"[NetAI] No .sln or .slnx file found above '{ProjectDir}'.");
            return false;
        }

        string projectName = Path.GetFileName(ProjectDir.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string testProjectDirectory = Path.Combine(
            solutionDirectory, "tests", "UnitTests", $"{projectName}.Tests");

        var collectedIssues = new List<string>();
        var orchestrator = new ResxTranslationOrchestrator();

        Compilation? compilation = BuildCompilationForOrchestrator();

        var csharpFiles = new List<string>();

        if (testXamlCs)
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

        Log.LogMessage(MessageImportance.High, $"[NetAI] C# files: {csharpFiles.Count()}");


        foreach (var sourceFilePath in csharpFiles)
        {
            try
            {

                Log.LogMessage(MessageImportance.High, "[NetAI] Starting orchestrator.ProcessProjectAsync...");
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
                    }, compilation, promptOnly: PromptOnly)
                ).GetAwaiter().GetResult();

                Log.LogMessage(MessageImportance.High, "[NetAI] Finished orchestrator.ProcessProjectAsync.");

                Log.LogMessage(MessageImportance.High, "[NetAI] Mode condition met. Starting test analysis...");

                if (result != "ok")
                {
                    Log.LogError($"[NetAI] Failed to process " +
                                 $"'{Path.GetFileName(sourceFilePath)}': {result}");
                    collectedIssues.Add($"[ERROR] {result}");
                    overallSuccess = false;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[NetAI] Critical error processing file " +
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

    private Compilation? BuildCompilationForOrchestrator()
    {
        var sourcePaths = SourceFiles
            .Select(i => i.ItemSpec)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Where(p => !p.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.EndsWith("AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sourcePaths.Count == 0)
        {
            Log.LogMessage(MessageImportance.High,
                "[NetAI] No @(Compile) items are available; compilation is not possible, " +
                "so analysis will use syntax only.");
            return null;
        }

        var referencePaths = ReferencePaths
            .Select(i => i.ItemSpec)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .ToList();

        bool usedFallback = false;
        if (referencePaths.Count == 0)
        {
            usedFallback = true;
            referencePaths = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToList();
        }

        try
        {
            var assemblyName = Path.GetFileNameWithoutExtension(
                ProjectDir.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(assemblyName))
                assemblyName = "TestabilityAnalysis";

            var compilation = RoslynDllTestabilityAnalyzer.BuildCompilation(
                sourcePaths,
                referencePaths,
                assemblyName);

            var errorCount = compilation.GetDiagnostics()
                .Count(d => d.Severity == DiagnosticSeverity.Error);

            Log.LogMessage(MessageImportance.High,
                $"[NetAI] Built compilation for orchestrator: " +
                $"{sourcePaths.Count} source files, {referencePaths.Count} references" +
                (usedFallback ? " (Fallback: geladene Assemblies)" : " (@(ReferencePath))") +
                $", {errorCount} compiler errors.");

            return compilation;
        }
        catch (Exception ex)
        {
            Log.LogWarning(
                $"[NetAI] Failed to build compilation: {ex.Message}; " +
                "analysis will use syntax only.");
            return null;
        }
    }

    private void TryOpenSummaryLog(List<string> issues)
    {
    }

    private static string? FindSolutionDirectory(string startDirectory)
    {
        var currentDirectory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (currentDirectory is not null)
        {
            if (currentDirectory.EnumerateFiles("*.sln").Any() ||
                currentDirectory.EnumerateFiles("*.slnx").Any())
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return null;
    }
}