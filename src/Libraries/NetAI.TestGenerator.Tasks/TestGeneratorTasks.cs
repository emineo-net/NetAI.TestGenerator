using System.Diagnostics;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models;
using Task = Microsoft.Build.Utilities.Task;

namespace NetAI.TestGenerator.Tasks;

/// <summary>Runs testability analysis and AI-assisted test generation as part of an MSBuild build.</summary>
public class TestGeneratorTask : Task
{

    private bool testDebugger = false;

    private readonly bool testXamlCs = true;

    /// <summary>Gets or sets the project directory supplied by MSBuild.</summary>
    [Required]
    public string ProjectDir { get; set; } = string.Empty;

    /// <summary>Gets or sets the active MSBuild configuration, such as <c>Debug</c> or <c>Release</c>.</summary>
    public string? CurrentConfiguration { get; set; }

    /// <summary>Gets or sets whether the current build is publishing the project.</summary>
    public bool IsPublishing { get; set; }

    /// <summary>Gets or sets whether to save generated drafts without compiling the test project.</summary>
    public bool PromptOnly { get; set; }

    /// <summary>Gets or sets source files provided by the MSBuild <c>Compile</c> item group.</summary>
    public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Gets or sets resolved assembly references used to build semantic analysis compilations.</summary>
    public ITaskItem[] ReferencePaths { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Gets or sets the directory where semantic analysis reports are written.</summary>
    public string? AnalysisOutputDirectory { get; set; }

    /// <summary>Gets or sets the MSBuild <c>$(DefineConstants)</c> value, e.g. <c>DEBUG;TRACE;NET10_0</c>.</summary>
    public string? DefineConstants { get; set; }

    /// <summary>Gets or sets whether the analyzed project uses WPF (<c>$(UseWPF)</c>).</summary>
    public bool UseWpf { get; set; }

    /// <summary>Gets or sets whether the analyzed project uses Windows Forms (<c>$(UseWindowsForms)</c>).</summary>
    public bool UseWindowsForms { get; set; }

    /// <summary>Gets or sets whether task errors fail the build.</summary>
    public bool FailOnError { get; set; } = false;

    /// <summary>Runs semantic analysis or test generation, depending on the inputs supplied by MSBuild.</summary>
    public override bool Execute()
    {

        if (ProjectDir.IndexOf("_wpftmp", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        var hasSemanticInputs = SourceFiles.Length > 0 && ReferencePaths.Length > 0 && !string.IsNullOrWhiteSpace(AnalysisOutputDirectory);

        return hasSemanticInputs ? ExecuteSemanticAnalysis() : ExecuteResxGeneration();
    }

    private bool ExecuteSemanticAnalysis()
    {
        try
        {
            var sourcePaths = SourceFiles.Select(i => i.ItemSpec).Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();

            var referencePaths = ReferencePaths.Select(i => i.ItemSpec).Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .ToList();

            Log.LogMessage(MessageImportance.High,
                $"[NetAI] Semantic analysis started: " + $"{sourcePaths.Count} source files, {referencePaths.Count} references.");

            var assemblyName = Path.GetFileName(ProjectDir.TrimEnd('/', '\\'));
            var parseOptions = BuildParseOptions();
            var compilation = RoslynDllTestabilityAnalyzer.BuildCompilation(sourcePaths, referencePaths,
                string.IsNullOrEmpty(assemblyName) ? "TestabilityAnalysis" : assemblyName, parseOptions);

            var compileErrorCount = compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);

            if (compileErrorCount > 0)
            {
                Log.LogMessage(MessageImportance.High,
                    $"[NetAI] Note: Compilation contains {compileErrorCount} errors. " +
                    "This is normal when source files from other projects are missing; " + "the analysis uses whatever can be resolved.");
            }

            var analyzer = new RoslynDllTestabilityAnalyzer();
            var reports = new List<TestabilityReport>();

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

                foreach (var method in methods)
                {
                    try
                    {
                        var report = System.Threading.Tasks.Task.Run(async () =>
                                await analyzer.AnalyzeFromCompilationAsync(compilation, method, CancellationToken.None)
                                    .ConfigureAwait(false))
                            .GetAwaiter().GetResult();

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

            var notTestableCount = reports.Count(r => r.Verdict != null && r.Verdict.StartsWith("NOT", StringComparison.Ordinal));

            Log.LogMessage(MessageImportance.High,
                $"[NetAI] Analyzed {reports.Count} methods; " + $"{notTestableCount} are not directly testable. Report: {reportPath}");

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"[NetAI] Semantic analysis failed: {ex.Message}");
            return false;
        }
    }

    private static void WriteAnalysisReport(IReadOnlyList<TestabilityReport> reports, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NetAI Testability Report");
        sb.AppendLine("========================");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Method count: {reports.Count}");
        sb.AppendLine();

        foreach (var group in reports.GroupBy(r => r.DocumentName ?? "(unnamed)").OrderBy(g => g.Key, StringComparer.Ordinal))
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
                    {
                        sb.AppendLine($"      - {rec}");
                    }
                }

                if (report.ReferencedTypes?.Count > 0)
                {
                    sb.AppendLine("    Referenced types:");
                    foreach (var t in report.ReferencedTypes)
                    {
                        sb.AppendLine($"      - {t.FullName} " + $"(Kind: {t.Kind}, Mockable: {t.Mockable}, " +
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
#if DEBUG
        if (!Debugger.IsAttached)
        {

            Debugger.Launch();
        }
#endif

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







        var currentConfiguration = (CurrentConfiguration ?? "Debug").Trim();
        var mode = (config.BuildConfigurationFilter ?? "All").Trim();

        var isAll = mode.Equals("All", StringComparison.OrdinalIgnoreCase);
        var matches = mode.Equals(currentConfiguration, StringComparison.OrdinalIgnoreCase);

        if (!isAll && !matches)
        {
            Log.LogMessage(
                MessageImportance.High,
                $"[NetAI] Skipped: BuildConfigurationFilter='{mode}', " +
                $"CurrentConfiguration='{currentConfiguration}'.");
            return true;
        }

        Log.LogMessage(
            MessageImportance.High,
            $"[NetAI] Mode condition met (filter='{mode}', current='{currentConfiguration}'). " +
            "Starting test analysis...");


        var solutionDirectory = FindSolutionDirectory(ProjectDir);
        if (solutionDirectory is null)
        {
            Log.LogError($"[NetAI] No .sln or .slnx file found above '{ProjectDir}'.");
            return false;
        }

        var projectName = Path.GetFileName(ProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var testProjectDirectory = Path.Combine(solutionDirectory, "tests", "UnitTests", $"{projectName}.Tests");

        var collectedIssues = new List<string>();
        ResxTranslationOrchestrator orchestrator;
        try
        {
            orchestrator = new ResxTranslationOrchestrator(config);
        }
        catch (ArgumentException ex)
        {
            Log.LogError($"[NetAI] Invalid framework settings in aisettings.json: {ex.Message}");
            return false;
        }

        var compilation = BuildCompilationForOrchestrator();

        var csharpFiles = new List<string>();

        if (testXamlCs)
        {
            csharpFiles = Directory.GetFiles(ProjectDir, "*.cs", SearchOption.AllDirectories).Select(file => Path.GetFullPath(file))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                               !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                               !file.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) && !file.Contains("_wpftmp") &&
                               !file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
                               !file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) && !file.Contains("Designer.cs") &&
                               !file.Contains("AiTranslatorRunner") && !file.Contains("AssemblyAttributes") &&
                               !file.Contains("AssemblyInfo")).ToList();
        }
        else
        {
            csharpFiles = Directory.GetFiles(ProjectDir, "*.cs", SearchOption.AllDirectories).Select(file => Path.GetFullPath(file))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                               !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                               !file.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) && !file.Contains("_wpftmp") &&
                               !file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
                               !file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) && !file.Contains("Designer.cs") &&
                               !file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) && !file.Contains("AiTranslatorRunner") &&
                               !file.Contains("AssemblyAttributes") && !file.Contains("AssemblyInfo")).ToList();
        }

        var overallSuccess = true;

        Log.LogMessage(MessageImportance.High, $"[NetAI] C# files: {csharpFiles.Count()}");

        foreach (var sourceFilePath in csharpFiles)
        {
            try
            {
                Log.LogMessage(MessageImportance.High, "[NetAI] Starting orchestrator.ProcessProjectAsync...");
                var result = System.Threading.Tasks.Task.Run(async () => await orchestrator.ProcessProjectAsync(sourceFilePath,
                    testProjectDirectory, message =>
                    {
                        if (string.IsNullOrWhiteSpace(message))
                        {
                            return;
                        }

                        var isError = message.Contains("[NetAI Error]") || message.Contains("Error:");
                        var isWarning = message.Contains("[NetAI Warning]") || message.Contains("Warning:");

                        if (isError)
                        {
                            var cleanMessage = message.Replace("[NetAI Error]", "").Replace("Error:", "").Trim();



                            if (FailOnError)
                            {
                                Log.LogError($"[NetAI] {cleanMessage}");
                            }
                            else
                            {
                                Log.LogWarning($"[NetAI] {cleanMessage}");
                            }

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
                    }, compilation, PromptOnly)).GetAwaiter().GetResult();

                Log.LogMessage(MessageImportance.High, "[NetAI] Finished orchestrator.ProcessProjectAsync.");

                if (result != "ok")
                {

                    var msg = $"[NetAI] Failed to process '{Path.GetFileName(sourceFilePath)}': {result}";
                    if (FailOnError)
                    {
                        Log.LogError(msg);
                    }
                    else
                    {
                        Log.LogWarning(msg);
                    }

                    collectedIssues.Add($"[ERROR] {result}");
                    overallSuccess = false;
                }
            }
            catch (Exception ex)
            {
                var msg = $"[NetAI] Critical error processing file " + $"'{Path.GetFileName(sourceFilePath)}': {ex.Message}";
                if (FailOnError)
                {
                    Log.LogError(msg);
                }
                else
                {
                    Log.LogWarning(msg);
                }

                collectedIssues.Add($"[CRITICAL] {ex.Message}");
                overallSuccess = false;
            }
        }

        var uniqueIssues = collectedIssues.Distinct().ToList();
        if (uniqueIssues.Count > 0)
        {
            TryOpenSummaryLog(uniqueIssues);
        }

        return FailOnError ? overallSuccess : true;
    }

    private CSharpParseOptions BuildParseOptions()
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(DefineConstants))
        {
            foreach (var raw in DefineConstants.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = raw.Trim();
                if (s.Length > 0)
                {
                    symbols.Add(s);
                }
            }
        }


        if (symbols.Count == 0)
        {
            symbols.Add("DEBUG");
            symbols.Add("TRACE");
            symbols.Add("NET");
        }


        if (UseWpf || UseWindowsForms)
        {
            symbols.Add("WINDOWS");
        }

        var symbolArray = symbols.ToArray();

        Log.LogMessage(MessageImportance.High, $"[NetAI] Preprocessor symbols: {string.Join(", ", symbolArray)}");

        return new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(symbolArray);
    }

    private Compilation? BuildCompilationForOrchestrator()
    {
        var sourcePaths = SourceFiles.Select(i => i.ItemSpec).Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Where(p => !p.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.EndsWith("AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)).ToList();

        if (sourcePaths.Count == 0)
        {
            Log.LogMessage(MessageImportance.High, "[NetAI] No @(Compile) items available; falling back to syntax-only analysis.");
            return null;
        }

        var referencePaths = ReferencePaths.Select(i => i.ItemSpec).Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var usedFallback = false;
        if (referencePaths.Count == 0)
        {
            usedFallback = true;
            referencePaths = BuildFallbackReferenceSet();
        }

        try
        {
            var assemblyName = Path.GetFileNameWithoutExtension(ProjectDir.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(assemblyName))
            {
                assemblyName = "TestabilityAnalysis";
            }

            var parseOptions = BuildParseOptions();

            var compilation = RoslynDllTestabilityAnalyzer.BuildCompilation(sourcePaths, referencePaths, assemblyName, parseOptions);

            var hasCorlib = compilation.GetTypeByMetadataName("System.Object") != null;
            var hasWpf = compilation.GetTypeByMetadataName("System.Windows.Window") != null;

            var errorCount = compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);

            Log.LogMessage(MessageImportance.High,
                $"[NetAI] Compilation: {sourcePaths.Count} files, {referencePaths.Count} refs" +
                (usedFallback ? " (fallback)" : " (@(ReferencePath))") + $", corlib={hasCorlib}, WPF={hasWpf}, {errorCount} errors.");

            return compilation;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[NetAI] Failed to build compilation: {ex.Message}; using syntax only.");
            return null;
        }
    }

    private static List<string> BuildFallbackReferenceSet()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var p in tpa.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                {
                    paths.Add(p);
                }
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic)
            {
                continue;
            }

            var loc = asm.Location;
            if (string.IsNullOrEmpty(loc))
            {
                continue;
            }

            if (File.Exists(loc))
            {
                paths.Add(loc);
            }
        }

        return paths.ToList();
    }

    private void TryOpenSummaryLog(List<string> issues)
    {
    }

    private static string? FindSolutionDirectory(string startDirectory)
    {
        var currentDirectory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (currentDirectory is not null)
        {
            if (currentDirectory.EnumerateFiles("*.sln").Any() || currentDirectory.EnumerateFiles("*.slnx").Any())
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return null;
    }
}