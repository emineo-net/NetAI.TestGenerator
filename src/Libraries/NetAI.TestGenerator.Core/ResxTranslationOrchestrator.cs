using DotNet10TestGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Models;
using NetAI.TestGenerator.Core.Models.Enums;
using NetAI.TestGenerator.Core.Services;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetAI.TestGenerator.Core;

/// <summary>Coordinates source analysis, AI-generated unit tests, and optional compile validation.</summary>
public class ResxTranslationOrchestrator
{
    private static readonly Regex TestCodeBlockRegex = new(
        @"```(?:csharp|cs)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TestGeneratorService _testGeneratorService;
    private readonly TestCodeBeautifier _testCodeBeautifier = new();

    public ResxTranslationOrchestrator(HttpClient? httpClient = null)
    {
        _testGeneratorService = new TestGeneratorService();
    }

    /// <summary>Generates tests for uncovered methods in the first class found in a source file.</summary>
    /// <param name="sourceFilePath">Path to the source file to analyze.</param>
    /// <param name="testProjectDirectory">Directory where generated test files and the test project are stored.</param>
    /// <param name="logInfo">Optional callback for progress and diagnostic messages.</param>
    /// <param name="compilation">
    /// Optional in-memory Roslyn compilation. Used only when neither
    /// <paramref name="solutionPath"/> nor <paramref name="projectPath"/> is provided.
    /// </param>
    /// <param name="promptOnly">When <see langword="true"/>, skips compile validation after saving each generated test.</param>
    /// <param name="solutionPath">
    /// Optional path to a .sln file. When set, MSBuild workspace loading is used and gives the most
    /// accurate semantic analysis (resolves NuGet, WPF, Directory.Build.props, project references).
    /// Requires net10.0; ignored on netstandard2.0.
    /// </param>
    /// <param name="projectPath">
    /// Optional path to a .csproj file. Same as <paramref name="solutionPath"/> but loads a single project.
    /// </param>
    /// <returns><c>"ok"</c> when processing completes, or an error message if processing fails.</returns>
    public async Task<string> ProcessProjectAsync(
        string sourceFilePath,
        string testProjectDirectory,
        Action<string>? logInfo = null,
        Compilation? compilation = null,
        bool promptOnly = false,
        string? solutionPath = null,
        string? projectPath = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return "Source file empty or missing.";

            string sourceCode;
            using (var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                sourceCode = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            // Prefer a compilation-owned syntax tree; only parse locally when no compilation is available.
            var sourceTree = compilation?.SyntaxTrees
                                 .FirstOrDefault(t => string.Equals(
                                     Path.GetFullPath(t.FilePath ?? ""),
                                     Path.GetFullPath(sourceFilePath),
                                     StringComparison.OrdinalIgnoreCase))
                             ?? CSharpSyntaxTree.ParseText(sourceCode, path: sourceFilePath);

            var sourceRoot = sourceTree.GetCompilationUnitRoot();

            var targetClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (targetClass == null) return "No class found in source file.";

            string className = targetClass.Identifier.Text;
            string? hostProjectDir = promptOnly ? FindProjectDirectory(sourceFilePath) : null;

            BuildLogger.BuildLog("\nhostProjectDir: " + hostProjectDir);

            string testClassName = $"{className}Tests";
            string testFilePath = Path.Combine(testProjectDirectory, $"{testClassName}.cs");

            BuildLogger.BuildLog("\ntestFilePath: " + testFilePath);

            logInfo?.Invoke($"[NetAI] Starting analysis for class: {className}");

            // NOTE: Disabled hard-coded path - uncomment and adapt locally if you need a clean slate.
            // File.Delete(@"C:\Users\steph\source\repos\NetAI.TestGenerator\tests\UnitTests\WpftranlationTestApp.Tests\MainWindowTests.cs");

            var existingTestMethods = _testGeneratorService.GetExistingTestMethods(testFilePath);
            var sourceMethods = targetClass.DescendantNodes().OfType<MethodDeclarationSyntax>();

            var localLlmClient = new LocalLlmClient();
            var aiPromptBuilderSimple = new AiPromptBuilderSimple();
            var testProjectManager = new TestProjectManager();

            var compilerService = new TestProjectManagerCompilerService(
                code => testProjectManager.SetupAndValidateTestAsync(
                    sourceFilePath, code, testProjectDirectoryOverride: testProjectDirectory));

            var testCodeProcessor = new TestCodeProcessor(compilerService);

            // --- Semantic analysis strategy selection -------------------------------
            // Priority: solution > project > in-memory compilation > none.
            RoslynDllTestabilityAnalyzer? semanticAnalyzer = null;

#if !NETSTANDARD2_0
            bool useMsbuild = !string.IsNullOrWhiteSpace(solutionPath)
                              || !string.IsNullOrWhiteSpace(projectPath);
#else
            bool useMsbuild = false;
#endif

            if (useMsbuild)
            {
                semanticAnalyzer = new RoslynDllTestabilityAnalyzer();
                var target = !string.IsNullOrWhiteSpace(solutionPath)
                    ? $"solution '{Path.GetFileName(solutionPath)}'"
                    : $"project '{Path.GetFileName(projectPath)}'";
                logInfo?.Invoke($"[NetAI] MSBuild workspace will be used ({target}); full project context available.");
            }
            else if (compilation != null)
            {
                semanticAnalyzer = new RoslynDllTestabilityAnalyzer();
                logInfo?.Invoke("[NetAI] Semantic analysis is available via in-memory compilation; AI prompts will include semantic context.");
            }
            else
            {
                logInfo?.Invoke("[NetAI] No compilation or MSBuild path was provided; using syntax-only analysis.");
            }

            // Project context is constant per file, so build it once.
            string projectContext = BuildProjectContextHint(sourceFilePath);

            foreach (var method in sourceMethods)
            {
                string methodName = method.Identifier.Text;

                bool testExists = existingTestMethods.Any(t => t.Contains(methodName, StringComparison.OrdinalIgnoreCase));
                if (testExists)
                {
                    logInfo?.Invoke($"[NetAI] Method '{methodName}' is already covered by an existing test. Skipping.");
                    continue;
                }

                logInfo?.Invoke($"[NetAI] Missing test detected for method: {methodName}. Triggering AI generation...");

                string classSkeleton = BuildClassSkeleton(targetClass, method);
                BuildLogger.BuildLog("\nclassSkeleton: " + classSkeleton);

                string semanticHint = await BuildSemanticHintAsync(
                    semanticAnalyzer,
                    compilation,
                    solutionPath,
                    projectPath,
                    sourceFilePath,
                    method,
                    logInfo).ConfigureAwait(false);

                BuildLogger.BuildLog("\nsemanticHint: " + semanticHint);

                logInfo?.Invoke($"[NetAI] semantic hint: {semanticHint}");

                string basePrompt = BuildBasePrompt(
                    className, methodName, semanticHint, projectContext, classSkeleton);

                BuildLogger.BuildLog("\nbasePrompt: " + basePrompt);

                if (promptOnly)
                {
                    string promptDirectory = Path.Combine(
                        hostProjectDir ?? Path.GetDirectoryName(sourceFilePath)!,
                        "obj", "netai", "prompts");
                    Directory.CreateDirectory(promptDirectory);
                    string promptPath = Path.Combine(promptDirectory, $"{className}.{methodName}.prompt.md");
                    File.WriteAllText(promptPath, basePrompt, Encoding.UTF8);
                    logInfo?.Invoke($"[NetAI] Prompt with semantic context saved: {promptPath}");
                }

                var newTestClassResponse = await localLlmClient.AskAsync(
                    basePrompt,
                    "You are a C# testing expert. Respond only with runnable C# code and no explanations.").ConfigureAwait(false);

                string testMethodCode = ExtractTestClass(newTestClassResponse);
                BuildLogger.BuildLog("\ntestMethodCode: " + testMethodCode);

                if (promptOnly)
                {
                    if (!File.Exists(testFilePath))
                    {
                        _testGeneratorService.CreateNewTestClassFile(
                            testFilePath, testClassName,
                            targetClass.Parent as NamespaceDeclarationSyntax,
                            testMethodCode);
                    }
                    else
                    {
                        _testGeneratorService.AppendMethodToExistingClassFile(testFilePath, testMethodCode);
                    }

                    existingTestMethods.Add(methodName);
                    logInfo?.Invoke($"[NetAI] Test draft for '{methodName}' saved; compile validation skipped.");
                    continue;
                }

                TestGenerationResult? result = null;
                bool isCompiledSuccessfully = false;

                int aiAttempts = 0;
                int envAttempts = 0;

                const int MaxAiRetries = 2;
                const int MaxEnvRetries = 2;

                while (true)
                {
                    if (aiAttempts >= MaxAiRetries)
                    {
                        logInfo?.Invoke($"[NetAI] Reached {MaxAiRetries} AI repair attempts for '{methodName}'. Giving up.");
                        break;
                    }

                    if (envAttempts >= MaxEnvRetries)
                    {
                        logInfo?.Invoke($"[NetAI] Reached {MaxEnvRetries} environment retries for '{methodName}'. " +
                                        $"Close Visual Studio / the running app and try again. " +
                                        $"Last known test class: {result?.TestClassPath}");
                        break;
                    }

                    string validationClassStructure = PrepareValidationStructure(
                        testClassName,
                        targetClass.Parent as NamespaceDeclarationSyntax,
                        testMethodCode);

                    validationClassStructure = await testCodeProcessor.ProcessTestClassAsync(
                        validationClassStructure,
                        testFramework: TestFramework.xUnit,
                        mockFramework: MockFramework.Unknown).ConfigureAwait(false);

                    result = await testProjectManager.SetupAndValidateTestAsync(
                        sourceFilePath,
                        validationClassStructure,
                        testProjectDirectoryOverride: testProjectDirectory).ConfigureAwait(false);

                    BuildLogger.BuildLog("\nresultErrors: " +
                        string.Join("\n", result.CompilerErrors ?? Array.Empty<string>()));

                    if (result.IsSuccess)
                    {
                        isCompiledSuccessfully = true;
                        if (!string.IsNullOrEmpty(result.TestClassCode))
                            testMethodCode = ExtractTestClass(result.TestClassCode!);
                        break;
                    }

                    if (result.IsEnvironmentIssue)
                    {
                        envAttempts++;
                        logInfo?.Invoke($"[NetAI] Environment issue while validating '{methodName}' " +
                                        $"(env retry {envAttempts}/{MaxEnvRetries}). " +
                                        $"Not asking the AI to repair – the test code itself is fine. " +
                                        $"Cause: a referenced assembly is locked by another process.");
                        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        continue;
                    }

                    aiAttempts++;
                    logInfo?.Invoke($"[NetAI] Test for '{methodName}' failed compilation " +
                                    $"(AI attempt {aiAttempts}/{MaxAiRetries}). Running AI repair loop...");

                    if (result.CompilerErrors?.Any(i =>
                            i.Contains("Could not automatically resolve a NuGet package for the namespace(s)")) == true)
                    {
                        logInfo?.Invoke("[NetAI] Warning: Missing NuGet dependencies detected in generated test.");
                    }

                    if (result.RequiresRegeneration)
                    {
                        logInfo?.Invoke("[NetAI] The manager flagged the test code for regeneration " +
                                        "(likely hallucinated types or invented members). " +
                                        "The repair prompt will include concrete hints.");
                    }

                    var errorsText = string.Join("\n", result.CompilerErrors ?? Array.Empty<string>());
                    BuildLogger.BuildLog("\nerrorsText: " + errorsText);
                    var codeForRepair = result.TestClassCode ?? validationClassStructure;

                    if (result.CompilerErrors?.Any() == true)
                    {
                        var roslynFixed = await _testCodeBeautifier.TryFixCompilerErrorsAsync(
                            codeForRepair, result.CompilerErrors).ConfigureAwait(false);

                        if (!string.Equals(roslynFixed, codeForRepair, StringComparison.Ordinal))
                        {
                            BuildLogger.BuildLog("\nroslynFixed: " + roslynFixed);
                            logInfo?.Invoke("[NetAI] Roslyn automatically added missing using directives; " +
                                            "retrying compilation without AI.");
                            testMethodCode = ExtractTestClass(roslynFixed);
                            continue;
                        }
                    }

                    var errorPrompt = aiPromptBuilderSimple.FixUnittestPromptSimple(errorsText, codeForRepair);
                    BuildLogger.BuildLog("\nerrorPrompt: " + errorPrompt);

                    var systemPrompt = "You are a precise C# compiler assistant. Your only task is to accurately fix " +
                                       "syntax and compilation errors in the provided C# code " +
                                       "and return runnable code without textual explanations.\n";

                    var correctedOutput = await localLlmClient.AskAsync(errorPrompt, systemPrompt).ConfigureAwait(false);
                    testMethodCode = ExtractTestClass(correctedOutput);
                    BuildLogger.BuildLog("\ntestMethodCode: " + testMethodCode);
                }

                BuildLogger.BuildLog("DONE");

                if (isCompiledSuccessfully)
                {
                    if (!File.Exists(testFilePath))
                    {
                        _testGeneratorService.CreateNewTestClassFile(
                            testFilePath, testClassName,
                            targetClass.Parent as NamespaceDeclarationSyntax,
                            testMethodCode);
                        logInfo?.Invoke($"[NetAI] Successfully created new test file and added '{methodName}_GeneratedTest'.");
                    }
                    else
                    {
                        _testGeneratorService.AppendMethodToExistingClassFile(testFilePath, testMethodCode);
                        logInfo?.Invoke($"[NetAI] Successfully appended test for '{methodName}' to existing test file.");
                    }

                    existingTestMethods.Add(methodName);
                }
                else
                {
                    logInfo?.Invoke($"[NetAI Warning] Could not generate a compilable test for '{methodName}' after {MaxAiRetries} retries.");
                    if (result?.CompilerErrors != null)
                        logInfo?.Invoke($"[NetAI] Final Compiler Errors:\n{string.Join("\n", result.CompilerErrors)}");
                }
            }

            return "ok";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------------------------------------------------------------- prompt building

    private static string BuildBasePrompt(
        string className,
        string methodName,
        string semanticHintXml,
        string projectContext,
        string classSkeleton)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a .NET testing expert. Create a precise xUnit test method (with [Fact]) for method '{methodName}' in class '{className}'.");
        sb.AppendLine();
        sb.AppendLine("Carefully read the automatically generated semantic analysis in <SemanticAnalysis> and the project context in <ProjectContext>.");
        sb.AppendLine("If a 'Verdict' or 'Blockers' entry identifies constraints (private, static deps, no public ctor), work around them pragmatically:");
        sb.AppendLine("- Use reflection for private members when unavoidable.");
        sb.AppendLine("- Use a recommended abstraction (e.g. System.IO.Abstractions) if it is listed and referenced.");
        sb.AppendLine("- Do NOT invent NuGet packages that are not in <ProjectContext>/<PackageReferences>.");
        sb.AppendLine("ALWAYS return a single xUnit test method.");
        sb.AppendLine("If a runnable test is technically impossible (e.g. 'async void'), still emit [Fact(Skip = \"<brief reason>\")] with the problematic code as a comment.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(projectContext))
        {
            sb.AppendLine(projectContext);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(semanticHintXml))
        {
            sb.AppendLine(semanticHintXml);
            sb.AppendLine();
        }
        sb.AppendLine("Relevant source context (usings, fields, method under test, and called helpers):");
        sb.AppendLine("<SourceCode>");
        sb.AppendLine(classSkeleton);
        sb.AppendLine("</SourceCode>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the semantic hint. Strategy (in order of priority):
    /// 1. MSBuild solution (net10.0 only) - full project context, no phantom errors.
    /// 2. MSBuild project   (net10.0 only).
    /// 3. In-memory compilation - fast, but missing external references.
    /// 4. No analysis.
    /// </summary>
    private static async Task<string> BuildSemanticHintAsync(
        RoslynDllTestabilityAnalyzer? analyzer,
        Compilation? compilation,
        string? solutionPath,
        string? projectPath,
        string sourceFilePath,
        MethodDeclarationSyntax methodDeclaration,
        Action<string>? logInfo)
    {
        if (analyzer == null)
            return string.Empty;

        string methodName = methodDeclaration.Identifier.Text;
        string fileName = Path.GetFileName(sourceFilePath);

        try
        {
            TestabilityReport report;

#if !NETSTANDARD2_0
            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                logInfo?.Invoke($"[NetAI] Loading solution '{Path.GetFileName(solutionPath)}' via MSBuild for '{methodName}'...");
                report = await analyzer.AnalyzeFromSolutionAsync(solutionPath, fileName, methodName)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(projectPath))
            {
                logInfo?.Invoke($"[NetAI] Loading project '{Path.GetFileName(projectPath)}' via MSBuild for '{methodName}'...");
                report = await analyzer.AnalyzeFromProjectAsync(projectPath, fileName, methodName)
                    .ConfigureAwait(false);
            }
            else
#endif
            if (compilation != null)
            {
                report = await analyzer.AnalyzeFromCompilationAsync(compilation, methodDeclaration)
                    .ConfigureAwait(false);
            }
            else
            {
                return string.Empty;
            }

            logInfo?.Invoke($"[NetAI] Semantic analysis for '{methodName}': {report.Verdict}");
            return FormatReportAsXml(report, sourceFilePath, methodName);
        }
        catch (InvalidOperationException ex)
        {
            logInfo?.Invoke($"[NetAI] Skipped semantic analysis for '{methodName}': {ex.Message}");
            return string.Empty;
        }
        catch (Exception ex)
        {
            logInfo?.Invoke($"[NetAI] Semantic analysis for '{methodName}' failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static string FormatReportAsXml(TestabilityReport report, string sourceFilePath, string methodName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<SemanticAnalysis>");
        sb.AppendLine($"  <Document name=\"{X(Path.GetFileName(sourceFilePath))}\" method=\"{X(methodName)}\" generatedAt=\"{report.GeneratedAt:O}\" />");

        if (report.Method is { } m)
        {
            sb.AppendLine($"  <Method name=\"{X(m.Name)}\" signature=\"{X(m.Signature)}\" accessibility=\"{X(m.Accessibility)}\" " +
                          $"returnType=\"{X(m.ReturnType)}\" isStatic=\"{m.IsStatic}\" isAsync=\"{m.IsAsync}\" " +
                          $"returnsVoid=\"{m.ReturnsVoid}\" isAsyncVoid=\"{m.IsAsyncVoid}\" isVirtual=\"{m.IsVirtual}\" " +
                          $"returnsTask=\"{m.ReturnsTask}\" hasCancellationToken=\"{m.HasCancellationToken}\" />");

            if (m.GenericParameters.Count > 0)
                sb.AppendLine($"    <GenericParameters>{X(string.Join(", ", m.GenericParameters))}</GenericParameters>");

            if (m.Attributes.Count > 0)
            {
                sb.AppendLine("    <Attributes>");
                foreach (var a in m.Attributes) sb.AppendLine($"      <Attribute>{X(a)}</Attribute>");
                sb.AppendLine("    </Attributes>");
            }

            if (m.ThrownExceptions.Count > 0)
            {
                sb.AppendLine("    <ThrownExceptions>");
                foreach (var t in m.ThrownExceptions) sb.AppendLine($"      <Exception>{X(t)}</Exception>");
                sb.AppendLine("    </ThrownExceptions>");
            }

            if (m.ContainingType is { } ct)
            {
                sb.AppendLine($"  <ContainingType fullName=\"{X(ct.FullName)}\" kind=\"{X(ct.Kind)}\" " +
                              $"accessibility=\"{X(ct.Accessibility)}\" isStatic=\"{ct.IsStatic}\" " +
                              $"isSealed=\"{ct.IsSealed}\" isAbstract=\"{ct.IsAbstract}\" mockable=\"{X(ct.Mockable)}\" />");
                if (!string.IsNullOrEmpty(ct.BaseType))
                    sb.AppendLine($"    <BaseType>{X(ct.BaseType)}</BaseType>");
                if (ct.Interfaces.Count > 0)
                    sb.AppendLine($"    <Interfaces>{X(string.Join(", ", ct.Interfaces))}</Interfaces>");
                if (ct.Constructors.Count > 0)
                    sb.AppendLine($"    <PublicCtors>{X(string.Join(" | ", ct.Constructors))}</PublicCtors>");
                else
                    sb.AppendLine("    <PublicCtors>none</PublicCtors>");
            }
        }

        sb.AppendLine($"  <Testability verdict=\"{X(report.Verdict)}\" isDirectlyTestable=\"{report.IsDirectlyTestable}\" />");

        if (report.Blockers.Count > 0)
        {
            sb.AppendLine("  <Blockers>");
            foreach (var b in report.Blockers) sb.AppendLine($"    <Blocker>{X(b)}</Blocker>");
            sb.AppendLine("  </Blockers>");
        }

        var relevant = report.ReferencedTypes?
            .Where(t => t.UsedStatically
                        || t.DependencyKind == DependencyKind.Interface
                        || t.DependencyKind == DependencyKind.AbstractClass
                        || !t.Namespace.StartsWith("System", StringComparison.Ordinal))
            .ToList() ?? new List<TypeFact>();

        if (relevant.Count > 0)
        {
            sb.AppendLine("  <Dependencies>");
            foreach (var t in relevant)
            {
                sb.AppendLine($"    <Dependency type=\"{X(t.FullName)}\" kind=\"{t.DependencyKind}\" " +
                              $"mockable=\"{X(t.Mockable)}\" usedStatically=\"{t.UsedStatically}\" " +
                              $"usages=\"{t.Usages}\" />");
                if (!string.IsNullOrEmpty(t.RecommendedAbstraction))
                    sb.AppendLine($"      <RecommendedAbstraction>{X(t.RecommendedAbstraction)}</RecommendedAbstraction>");
                if (!string.IsNullOrEmpty(t.RecommendationReason))
                    sb.AppendLine($"      <Reason>{X(t.RecommendationReason)}</Reason>");
            }
            sb.AppendLine("  </Dependencies>");
        }

        if (report.AnalyzedCallGraph.Count > 1)
        {
            sb.AppendLine("  <CallGraph>");
            foreach (var c in report.AnalyzedCallGraph)
                sb.AppendLine($"    <Call>{X(c)}</Call>");
            sb.AppendLine("  </CallGraph>");
        }

        if (report.Recommendations.Count > 0)
        {
            sb.AppendLine("  <Recommendations>");
            foreach (var r in report.Recommendations)
                sb.AppendLine($"    <Recommendation>{X(r)}</Recommendation>");
            sb.AppendLine("  </Recommendations>");
        }

        if (report.CompilationErrors.Count > 0)
        {
            sb.AppendLine($"  <SourceCompilationErrors count=\"{report.CompilationErrors.Count}\" " +
                          "note=\"Errors in the SOURCE project, not the test. Missing framework references " +
                          "(e.g. WPF) are common here and unrelated to the generated test.\">");
            foreach (var e in report.CompilationErrors.Take(3))
                sb.AppendLine($"    <Error>{X(e)}</Error>");
            sb.AppendLine("  </SourceCompilationErrors>");
        }

        sb.AppendLine("</SemanticAnalysis>");
        return sb.ToString();
    }

    private static string BuildProjectContextHint(string sourceFilePath)
    {
        try
        {
            var csproj = FindProjectFile(sourceFilePath);
            if (csproj == null) return string.Empty;

            var doc = XDocument.Load(csproj);
            string? Val(string name) =>
                doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

            var sb = new StringBuilder();
            sb.AppendLine("<ProjectContext>");
            sb.AppendLine($"  <ProjectFile>{X(Path.GetFileName(csproj))}</ProjectFile>");
            AppendIfSet(sb, "TargetFramework", Val("TargetFramework") ?? Val("TargetFrameworks"));
            AppendIfSet(sb, "LangVersion", Val("LangVersion"));
            AppendIfSet(sb, "Nullable", Val("Nullable"));
            AppendIfSet(sb, "ImplicitUsings", Val("ImplicitUsings"));

            var packages = doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => new
                {
                    Name = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "",
                    Version = e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? ""
                })
                .Where(p => !string.IsNullOrEmpty(p.Name))
                .ToList();

            if (packages.Count > 0)
            {
                sb.AppendLine("  <PackageReferences>");
                foreach (var p in packages)
                    sb.AppendLine($"    <Package id=\"{X(p.Name)}\" version=\"{X(p.Version)}\" />");
                sb.AppendLine("  </PackageReferences>");
            }

            var projRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (projRefs.Count > 0)
            {
                sb.AppendLine("  <ProjectReferences>");
                foreach (var r in projRefs)
                    sb.AppendLine($"    <ProjectReference>{X(Path.GetFileName(r))}</ProjectReference>");
                sb.AppendLine("  </ProjectReferences>");
            }

            sb.AppendLine("</ProjectContext>");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AppendIfSet(StringBuilder sb, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"  <{name}>{X(value)}</{name}>");
    }

    private static string X(string? s) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;")
         .Replace("'", "&apos;");

    // ---------------------------------------------------------------- skeleton

    private string PrepareValidationStructure(
        string testClassName,
        NamespaceDeclarationSyntax? originalNamespace,
        string methodCode)
    {
        string namespaceName = originalNamespace?.Name.ToString() ?? "NetAI.Generated.Tests";
        if (!namespaceName.EndsWith(".Tests")) namespaceName += ".Tests";

        return $@"using Xunit;
namespace {namespaceName}
{{
    public class {testClassName}
    {{
        {methodCode}
    }}
}}";
    }

    private static string BuildClassSkeleton(
        ClassDeclarationSyntax targetClass,
        MethodDeclarationSyntax targetMethod,
        bool includeProperties = true,
        bool includeConstructors = true,
        bool includeRecursiveHelpers = true)
    {
        var sb = new StringBuilder();

        var usings = targetClass.SyntaxTree.GetCompilationUnitRoot().Usings;
        if (usings.Any())
        {
            foreach (var u in usings)
                sb.AppendLine(u.ToFullString().TrimEnd());
            sb.AppendLine();
        }

        foreach (var field in targetClass.Members.OfType<FieldDeclarationSyntax>())
            sb.AppendLine(field.ToFullString().TrimEnd());

        if (includeProperties)
            foreach (var prop in targetClass.Members.OfType<PropertyDeclarationSyntax>())
                sb.AppendLine(prop.ToFullString().TrimEnd());

        if (includeConstructors)
            foreach (var ctor in targetClass.Members.OfType<ConstructorDeclarationSyntax>())
                sb.AppendLine(ctor.ToFullString().TrimEnd());

        sb.AppendLine();
        sb.AppendLine(targetMethod.ToFullString().TrimEnd());

        var collectedHelpers = new HashSet<string>(StringComparer.Ordinal);
        var helperSb = new StringBuilder();

        void CollectHelpers(MethodDeclarationSyntax method)
        {
            foreach (var calledName in GetCalledMethodNames(method))
            {
                if (!collectedHelpers.Add(calledName)) continue;

                var helper = targetClass.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == calledName);

                if (helper is null) continue;

                helperSb.AppendLine(helper.ToFullString().TrimEnd());

                if (includeRecursiveHelpers)
                    CollectHelpers(helper);
            }
        }

        CollectHelpers(targetMethod);

        if (helperSb.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("// --- Helper methods called by the method above ---");
            sb.Append(helperSb);
        }

        return sb.ToString().Trim();
    }

    private static IEnumerable<string> GetCalledMethodNames(MethodDeclarationSyntax method)
    {
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            switch (invocation.Expression)
            {
                case IdentifierNameSyntax id:
                    yield return id.Identifier.Text;
                    break;
                case MemberAccessExpressionSyntax ma:
                    yield return ma.Name.Identifier.Text;
                    break;
                case GenericNameSyntax gen:
                    yield return gen.Identifier.Text;
                    break;
            }
        }
    }

    /// <summary>Extracts method declarations from an AI response, including responses wrapped in a code fence.</summary>
    public static string ExtractTestClass(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse)) return string.Empty;

        var match = TestCodeBlockRegex.Match(aiResponse);
        string rawCode = match.Success ? match.Groups[1].Value : aiResponse;

        var tree = CSharpSyntaxTree.ParseText(rawCode);
        var root = tree.GetCompilationUnitRoot();

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        if (methods.Count == 0) return rawCode.Trim();

        var sb = new StringBuilder();
        foreach (var method in methods)
            sb.AppendLine(method.ToFullString());

        return sb.ToString().Trim();
    }

    private static string? FindProjectDirectory(string filePath)
    {
        try
        {
            var currentDir = Path.GetDirectoryName(filePath);
            while (currentDir != null)
            {
                if (Directory.EnumerateFiles(currentDir, "*.csproj").Any())
                    return currentDir;
                currentDir = Directory.GetParent(currentDir)?.FullName;
            }
        }
        catch { }
        return null;
    }

    private static string? FindProjectFile(string filePath)
    {
        try
        {
            var currentDir = Path.GetDirectoryName(filePath);
            while (currentDir != null)
            {
                var csproj = Directory.EnumerateFiles(currentDir, "*.csproj").FirstOrDefault();
                if (csproj != null) return csproj;
                currentDir = Directory.GetParent(currentDir)?.FullName;
            }
        }
        catch { }
        return null;
    }
}