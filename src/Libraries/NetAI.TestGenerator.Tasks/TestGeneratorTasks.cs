using System.Text;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Infrastructure;
using NetAI.TestGenerator.Core.Models;
using Task = Microsoft.Build.Utilities.Task;

namespace NetAI.TestGenerator.Tasks;

/// <summary>Runs testability analysis and AI-assisted test generation as part of an MSBuild build.</summary>
public class TestGeneratorTask : Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>Gets or sets what to do: <c>Generate</c> (default) or <c>Analyze</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the project directory supplied by MSBuild.</summary>
    [Required]
    public string ProjectDir { get; set; } = string.Empty;

    /// <summary>Gets or sets the MSBuild project name (<c>$(MSBuildProjectName)</c>).</summary>
    public string? ProjectName { get; set; }

    /// <summary>Gets or sets the assembly name (<c>$(AssemblyName)</c>).</summary>
    public string? AssemblyName { get; set; }

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

    /// <summary>Gets or sets whether task errors fail the build. When false, problems are reported as warnings.</summary>
    public bool FailOnError { get; set; } = false;

    private string EffectiveProjectName =>
        !string.IsNullOrWhiteSpace(ProjectName)
            ? ProjectName!
            : Path.GetFileName(ProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private string EffectiveAssemblyName =>
        !string.IsNullOrWhiteSpace(AssemblyName) ? AssemblyName! : string.IsNullOrEmpty(EffectiveProjectName) ? "TestabilityAnalysis" : EffectiveProjectName;

    /// <summary>Requests cancellation (Ctrl+C in the console, Cancel in Visual Studio).</summary>
    public void Cancel() => _cancellation.Cancel();

    /// <summary>Runs semantic analysis or test generation, depending on <see cref="Mode"/>.</summary>
    public override bool Execute()
    {
        if (ProjectDir.IndexOf("_wpftmp", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        try
        {
            if (string.Equals(Mode, "Analyze", StringComparison.OrdinalIgnoreCase))
            {
                ExecuteSemanticAnalysis();
            }
            else
            {
                ExecuteTestGeneration();
            }
        }
        catch (OperationCanceledException)
        {
            Log.LogWarning("[NetAI] Test generation was cancelled.");
        }
        catch (Exception ex)
        {
            ReportIssue($"[NetAI] Unexpected failure: {ex.Message}");
            Log.LogMessage(MessageImportance.Low, ex.ToString());
        }

        // Errors are only logged when FailOnError is set, so this keeps the build green otherwise.
        return !Log.HasLoggedErrors;
    }

    /// <summary>Logs an error when <see cref="FailOnError"/> is set, otherwise a warning.</summary>
    private void ReportIssue(string message)
    {
        if (FailOnError)
        {
            Log.LogError(message);
        }
        else
        {
            Log.LogWarning(message);
        }
    }

    private void ExecuteSemanticAnalysis()
    {
        if (string.IsNullOrWhiteSpace(AnalysisOutputDirectory))
        {
            ReportIssue("[NetAI] Semantic analysis requires AnalysisOutputDirectory.");
            return;
        }

        var sourcePaths = GetSourcePaths();
        var referencePaths = GetReferencePaths();

        Log.LogMessage(MessageImportance.High,
            $"[NetAI] Semantic analysis started: {sourcePaths.Count} source files, {referencePaths.Count} references.");

        var compilation = RoslynDllTestabilityAnalyzer.BuildCompilation(sourcePaths, referencePaths, EffectiveAssemblyName, BuildParseOptions());

        var analyzer = new RoslynDllTestabilityAnalyzer();
        var reports = new List<TestabilityReport>();

        // One background task for the whole loop instead of a Task.Run per method.
        System.Threading.Tasks.Task.Run(async () =>
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                foreach (var method in (await tree.GetRootAsync(_cancellation.Token).ConfigureAwait(false)).DescendantNodes()
                             .OfType<MethodDeclarationSyntax>())
                {
                    _cancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        reports.Add(await analyzer.AnalyzeFromCompilationAsync(compilation, method, _cancellation.Token).ConfigureAwait(false));
                    }
                    catch (InvalidOperationException)
                    {
                        // Method could not be resolved in the compilation – skip it.
                    }
                }
            }
        }).GetAwaiter().GetResult();

        Directory.CreateDirectory(AnalysisOutputDirectory!);
        var reportPath = Path.Combine(AnalysisOutputDirectory!, "testability-report.txt");
        WriteAnalysisReport(reports, reportPath);

        var notTestableCount = reports.Count(r => r.Verdict != null && r.Verdict.StartsWith("NOT", StringComparison.Ordinal));

        Log.LogMessage(MessageImportance.High,
            $"[NetAI] Analyzed {reports.Count} methods; {notTestableCount} are not directly testable. Report: {reportPath}");
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
                        sb.AppendLine($"      - {t.FullName} (Kind: {t.Kind}, Mockable: {t.Mockable}, Static: {t.UsedStatically})");
                    }
                }

                sb.AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    private void ExecuteTestGeneration()
    {
        AiTestingConfig config;
        try
        {
            config = AiSettingsLoader.Load(ProjectDir);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or Newtonsoft.Json.JsonException)
        {
            ReportIssue($"[NetAI] Could not load aisettings.json: {ex.Message}");
            return;
        }

        var currentConfiguration = (CurrentConfiguration ?? "Debug").Trim();
        var filter = string.IsNullOrWhiteSpace(config.BuildConfigurationFilter) ? "All" : config.BuildConfigurationFilter.Trim();

        if (!MatchesConfigurationFilter(filter, currentConfiguration, IsPublishing))
        {
            Log.LogMessage(MessageImportance.High,
                $"[NetAI] Skipped: buildConfigurationFilter='{filter}', configuration='{currentConfiguration}', publishing={IsPublishing}.");
            return;
        }

        Log.LogMessage(MessageImportance.High,
            $"[NetAI] Filter matched (filter='{filter}', configuration='{currentConfiguration}'). Starting test generation...");

        var testProjectDirectory = TestProjectLocator.Resolve(ProjectDir, EffectiveProjectName, config);
        if (testProjectDirectory is null)
        {
            ReportIssue($"[NetAI] No test project and no .sln/.slnx file found above '{ProjectDir}'; cannot decide where to create tests.");
            return;
        }

        TetsGeneratorOrchestrator orchestrator;
        try
        {
            orchestrator = new TetsGeneratorOrchestrator(config);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ReportIssue($"[NetAI] Invalid settings in aisettings.json: {ex.Message}");
            return;
        }

        using (orchestrator)
        {
            var compilation = BuildCompilationForOrchestrator();

            // Use exactly the files MSBuild compiles (respects <Compile Remove>, linked files, nested projects).
            var csharpFiles = GetSourcePaths()
                .Where(f => SourceFileFilter.IsCandidate(f, ProjectDir))
                .Where(f => f.IndexOf("AiTestGeneratorRunner", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();

            Log.LogMessage(MessageImportance.High, $"[NetAI] C# files: {csharpFiles.Count}");

            foreach (var sourceFilePath in csharpFiles)
            {
                _cancellation.Token.ThrowIfCancellationRequested();

                Log.LogMessage(MessageImportance.Normal, $"[NetAI] Processing '{Path.GetFileName(sourceFilePath)}'...");

                var result = System.Threading.Tasks.Task.Run(() => orchestrator.ProcessProjectAsync(sourceFilePath, testProjectDirectory,
                        LogFromCore, compilation, PromptOnly, cancellationToken: _cancellation.Token))
                    .GetAwaiter().GetResult();

                if (result.Status is FileGenerationStatus.Failed or FileGenerationStatus.PartialSuccess)
                {
                    ReportIssue($"[NetAI] '{Path.GetFileName(sourceFilePath)}': {result.Message}");
                }
                else
                {
                    Log.LogMessage(MessageImportance.High, $"[NetAI] '{Path.GetFileName(sourceFilePath)}': {result.Message}");
                }
            }
        }
    }

    private void LogFromCore(GeneratorLogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        switch (level)
        {
            case GeneratorLogLevel.Error:
            case GeneratorLogLevel.Warning:
                // Per-method problems are warnings; the per-file summary decides about errors (FailOnError).
                Log.LogWarning(message);
                break;
            case GeneratorLogLevel.Debug:
                Log.LogMessage(MessageImportance.Low, message);
                break;
            default:
                Log.LogMessage(MessageImportance.High, message);
                break;
        }
    }

    /// <summary>
    /// Supports "All", a single configuration, a comma/semicolon separated list, and "Publish" (matches publish builds).
    /// </summary>
    internal static bool MatchesConfigurationFilter(string filter, string configuration, bool isPublishing)
    {
        foreach (var entry in filter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()))
        {
            if (entry.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                entry.Equals(configuration, StringComparison.OrdinalIgnoreCase) ||
                (isPublishing && entry.Equals("Publish", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private List<string> GetSourcePaths() =>
        SourceFiles.Select(i => i.GetMetadata("FullPath"))
            .Select(p => string.IsNullOrWhiteSpace(p) ? null : p)
            .Where(p => p is not null && File.Exists(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<string> GetReferencePaths() =>
        ReferencePaths.Select(i => i.ItemSpec)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private CSharpParseOptions BuildParseOptions()
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(DefineConstants))
        {
            foreach (var raw in DefineConstants!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
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

        Log.LogMessage(MessageImportance.Normal, $"[NetAI] Preprocessor symbols: {string.Join(", ", symbols)}");

        return new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(symbols);
    }

    private Compilation? BuildCompilationForOrchestrator()
    {
        var sourcePaths = GetSourcePaths()
            .Where(p => !p.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.EndsWith("AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)).ToList();

        if (sourcePaths.Count == 0)
        {
            Log.LogMessage(MessageImportance.High, "[NetAI] No @(Compile) items available; falling back to syntax-only analysis.");
            return null;
        }

        var referencePaths = GetReferencePaths();

        var usedFallback = false;
        if (referencePaths.Count == 0)
        {
            usedFallback = true;
            referencePaths = BuildFallbackReferenceSet();
        }

        try
        {
            var compilation = RoslynDllTestabilityAnalyzer.BuildCompilation(sourcePaths, referencePaths, EffectiveAssemblyName,
                BuildParseOptions());

            var hasCorlib = compilation.GetTypeByMetadataName("System.Object") != null;
            var hasWpf = compilation.GetTypeByMetadataName("System.Windows.Window") != null;

            // Note: no compilation.GetDiagnostics() here – it binds the whole project just to print a number.
            Log.LogMessage(MessageImportance.High,
                $"[NetAI] Compilation: {sourcePaths.Count} files, {referencePaths.Count} refs" +
                (usedFallback ? " (fallback)" : " (@(ReferencePath))") + $", corlib={hasCorlib}, WPF={hasWpf}.");

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

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && tpa.Length > 0)
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
            if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
            {
                paths.Add(asm.Location);
            }
        }

        return paths.ToList();
    }
}
