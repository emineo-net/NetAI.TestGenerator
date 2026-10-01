using DotNet10TestGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Config;
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
    private readonly TestFramework _testFramework;
    private readonly MockFramework _mockFramework;

    /// <summary>Creates an orchestrator for generating tests from source files.</summary>
    /// <param name="httpClient">Reserved for custom transport support; the current implementation uses <see cref="LocalLlmClient"/>.</param>
    public ResxTranslationOrchestrator(HttpClient? httpClient = null)
        : this(TestFramework.xUnit, MockFramework.Unknown, httpClient)
    {
    }

    /// <summary>Creates an orchestrator using the configured test and mocking frameworks.</summary>
    /// <param name="config">Settings that select the test and mocking frameworks.</param>
    /// <param name="httpClient">Reserved for custom transport support; the current implementation uses <see cref="LocalLlmClient"/>.</param>
    /// <exception cref="ArgumentException">A configured test or mocking framework is unsupported.</exception>
    public ResxTranslationOrchestrator(AiTestingConfig config, HttpClient? httpClient = null)
        : this(
            ParseTestFramework(config),
            ParseMockFramework(config),
            httpClient)
    {
    }

    private ResxTranslationOrchestrator(
        TestFramework testFramework,
        MockFramework mockFramework,
        HttpClient? httpClient)
    {
        _testGeneratorService = new TestGeneratorService();
        _testFramework = testFramework;
        _mockFramework = mockFramework;
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
    /// Requires net10.0; ignored on netstandard2.0. The solution is loaded once per file.
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
        IDisposable? msbuildHandle = null;

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
            var testProjectManager = new TestProjectManager(testFramework: _testFramework, mockFramework: _mockFramework);

            var compilerService = new TestProjectManagerCompilerService(
                code => testProjectManager.SetupAndValidateTestAsync(
                    sourceFilePath,
                    code,
                    testTemplate: GetTestTemplate(_testFramework),
                    testProjectDirectoryOverride: testProjectDirectory));

            var testCodeProcessor = new TestCodeProcessor(compilerService);

            // --- Semantic analysis strategy selection -------------------------------
            // Priority: MSBuild (solution > project) > in-memory compilation > none.
            // MSBuild is loaded ONCE per file, then reused for every method.
            RoslynDllTestabilityAnalyzer? semanticAnalyzer = null;
            Compilation? effectiveCompilation = compilation;

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

#if !NETSTANDARD2_0
                try
                {
                    logInfo?.Invoke($"[NetAI] Loading {target} via MSBuild (once per file)...");
                    var loaded = await semanticAnalyzer.LoadCompilationFromMsbuildAsync(
                            solutionPath,
                            projectPath,
                            Path.GetFileName(sourceFilePath),
                            logInfo)
                        .ConfigureAwait(false);

                    msbuildHandle = loaded.Workspace;
                    effectiveCompilation = loaded.Compilation;

                    if (effectiveCompilation == null)
                    {
                        logInfo?.Invoke("[NetAI] MSBuild returned no compilation; falling back to syntax-only.");
                    }
                    else
                    {
                        var treeCount = effectiveCompilation.SyntaxTrees.Count();
                        logInfo?.Invoke($"[NetAI] MSBuild context loaded ({target}); {treeCount} syntax trees.");
                    }
                }
                catch (Exception ex)
                {
                    logInfo?.Invoke($"[NetAI] MSBuild load failed: {ex.Message}; falling back to in-memory compilation.");
                    msbuildHandle?.Dispose();
                    msbuildHandle = null;
                    effectiveCompilation = compilation;
                }
#endif
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
            string projectContext = BuildProjectContextHint(
                sourceFilePath,
                testProjectDirectory,
                _testFramework,
                _mockFramework);

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
                    effectiveCompilation,
                    method,
                    logInfo).ConfigureAwait(false);

                BuildLogger.BuildLog("\nsemanticHint: " + semanticHint);

                string basePrompt = BuildBasePrompt(
                    className,
                    methodName,
                    semanticHint,
                    projectContext,
                    classSkeleton);

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
                            testMethodCode,
                            frameworkUsing: $"using {GetTestFrameworkNamespace(_testFramework)};");
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
                        testFramework: _testFramework,
                        mockFramework: _mockFramework).ConfigureAwait(false);

                    result = await testProjectManager.SetupAndValidateTestAsync(
                        sourceFilePath,
                        validationClassStructure,
                        testTemplate: GetTestTemplate(_testFramework),
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
                            codeForRepair, result.CompilerErrors, _testFramework, _mockFramework).ConfigureAwait(false);

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
                            testMethodCode,
                            frameworkUsing: $"using {GetTestFrameworkNamespace(_testFramework)};");
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
        finally
        {
            try { msbuildHandle?.Dispose(); } catch { /* ignore */ }
        }
    }

    // ---------------------------------------------------------------- prompt building

    private string BuildBasePrompt(
     string className,
     string methodName,
     string semanticHintXml,
     string projectContext,
     string classSkeleton)
    {
        string frameworkName = GetTestFrameworkName(_testFramework);
        string testAttribute = GetTestAttribute(_testFramework);
        string mockFrameworkInstruction = _mockFramework == MockFramework.Unknown
            ? "Do not introduce a mocking library unless it is listed in <TestProject>."
            : $"When mocking is needed, use {_mockFramework} only if it is listed in <TestProject>.";

        var sb = new StringBuilder();

        // --- Rolle & Ziel (klarer formuliert, "one test method OR skip") ---
        sb.AppendLine($"You are a .NET testing expert working with {frameworkName}.");
        sb.AppendLine($"Generate the test for method '{methodName}' in class '{className}' according to <SuggestedTestStrategy>.");
        sb.AppendLine($"- If the strategy is 'Generate', produce one test method using {testAttribute}.");
        sb.AppendLine($"- If the strategy is 'Skip', produce one test method with the framework's skip/ignore attribute; put the body in a comment only.");
        sb.AppendLine($"- If the strategy is 'RefactorFirst', produce a Skip test AND list the required source refactorings as comments above the test.");
        sb.AppendLine();

        // --- Source of truth ---
        sb.AppendLine("Use <SemanticAnalysis> as the source of truth for method and dependency facts, and follow <SuggestedTestStrategy> exactly.");
        sb.AppendLine("Do not use reflection, dynamic invocation, or workaround code for private/static/async-void members.");
        sb.AppendLine();

        // --- Testprojekt-Regeln ---
        sb.AppendLine("Rules for the test project:");
        sb.AppendLine($"- You MAY create a new test class in the test project (naming: <ClassUnderTest>Tests).");
        sb.AppendLine($"- Do NOT invent source types, members, namespaces, project references, or NuGet packages.");
        sb.AppendLine($"- Do NOT invent types that are not present in <ProjectContext> or <SemanticAnalysis>.");
        sb.AppendLine($"- {mockFrameworkInstruction}");
        sb.AppendLine("- Use the selected test framework from <SelectedFrameworks>; the test project uses that framework's template.");
        sb.AppendLine("- Use a mocking library or helper only if it is listed in <TestProject>.");
        sb.AppendLine("- Keep source-code refactoring advice separate from the generated test; do not modify or assume changes to the source project.");
        sb.AppendLine("- If the method under test returns Task or Task<T>, make the test method async Task. Do NOT make the test method async for 'async void' methods; those are covered by <SuggestedTestStrategy> (Skip).");
        sb.AppendLine();

        // --- Ausgabeformat ---
        sb.AppendLine("Output format:");
        sb.AppendLine("- Return ONLY compilable C# code (no explanations, no prose, no TODO markers outside comments).");
        sb.AppendLine($"- Include \"using {GetTestFrameworkNamespace(_testFramework)};\" at the top of the generated code UNLESS the test project's <ProjectContext> already lists that namespace under <GlobalUsings>.");
        sb.AppendLine("- Use top-level usings consistent with ImplicitUsings/Nullable settings from <ProjectContext>.");
        sb.AppendLine("- Use the test project's <RootNamespace> from <ProjectContext> verbatim when present.");
        sb.AppendLine("- If <RootNamespace> is absent, derive the namespace from the test project file name (without the .csproj extension). Do not invent any other namespace.");
        sb.AppendLine("- Include [Fact] (or the framework-specific attribute) exactly once.");
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
    /// Builds the semantic hint. The compilation has already been prepared by
    /// <see cref="ProcessProjectAsync"/> (in-memory or MSBuild-loaded), so this
    /// method just runs the analyzer for the given method.
    /// </summary>
    private async Task<string> BuildSemanticHintAsync(
        RoslynDllTestabilityAnalyzer? analyzer,
        Compilation? compilation,
        MethodDeclarationSyntax methodDeclaration,
        Action<string>? logInfo)
    {
        if (analyzer == null || compilation == null)
            return string.Empty;

        string methodName = methodDeclaration.Identifier.Text;

        try
        {
            var report = await analyzer.AnalyzeFromCompilationAsync(compilation, methodDeclaration)
                .ConfigureAwait(false);

            logInfo?.Invoke($"[NetAI] Semantic analysis for '{methodName}': {report.Verdict}");

            var filePath = methodDeclaration.SyntaxTree.FilePath ?? methodName;
            return FormatReportAsXml(report, filePath, methodName);
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

    private string FormatReportAsXml(TestabilityReport report, string sourceFilePath, string methodName)
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

        sb.AppendLine("  <Recommendations>");
        sb.AppendLine("    <SourceRefactoring>");
        foreach (var recommendation in report.SourceRefactoringRecommendations)
            sb.AppendLine($"      <Recommendation>{X(recommendation)}</Recommendation>");
        sb.AppendLine("    </SourceRefactoring>");
        sb.AppendLine("    <TestStrategy>");
        foreach (var recommendation in report.TestStrategyRecommendations)
            sb.AppendLine($"      <Recommendation>{X(recommendation)}</Recommendation>");
        sb.AppendLine("    </TestStrategy>");
        sb.AppendLine("  </Recommendations>");

        AppendSuggestedTestStrategy(sb, report);


        if (report.CompilationErrors.Count > 0)
        {
            sb.AppendLine("  <SourceCompilationErrors " +
                          "note=\"Analyzer-host artifacts (e.g. missing WPF reference in the analyzer). " +
                          "Provided as context only. Do NOT fix them, do NOT work around them, and do NOT " +
                          "let them change the test design; <SuggestedTestStrategy> is authoritative.\">");
            foreach (var e in report.CompilationErrors)
                sb.AppendLine($"    <!-- {X(e)} -->");
            sb.AppendLine("  </SourceCompilationErrors>");
        }

        sb.AppendLine("</SemanticAnalysis>");
        return sb.ToString();
    }

    private void AppendSuggestedTestStrategy(StringBuilder sb, TestabilityReport report)
    {
        var method = report.Method;
        bool inaccessible = method.Accessibility is "Private" or "Protected" or "ProtectedAndInternal";
        bool hasStaticDependency = report.ReferencedTypes.Any(type => type.UsedStatically);
        bool privateAsyncVoidWithStaticDependency =
            method.IsAsyncVoid && inaccessible && hasStaticDependency;

        if (method.IsAsyncVoid)
        {
            string reason = privateAsyncVoidWithStaticDependency
                ? "private async void method with static dependencies is not safely invokable from a unit test"
                : "async void cannot be awaited reliably by a unit test";

            sb.AppendLine($"  <SuggestedTestStrategy action=\"Skip\" testFramework=\"{X(GetTestFrameworkName(_testFramework))}\">");
            sb.AppendLine($"    <Instruction>Emit {X(GetSkipAttribute(reason))}. Include any illustrative body only as a comment.</Instruction>");
            if (privateAsyncVoidWithStaticDependency)
                sb.AppendLine("    <Constraint>Do not use reflection. Do not invoke the handler or perform real static I/O.</Constraint>");
            else
                sb.AppendLine("    <Constraint>Do not invoke the async void method from the test.</Constraint>");
            sb.AppendLine("  </SuggestedTestStrategy>");
            return;
        }

        if (report.IsDirectlyTestable)
        {
            sb.AppendLine($"  <SuggestedTestStrategy action=\"Direct\" testFramework=\"{X(GetTestFrameworkName(_testFramework))}\">");
            sb.AppendLine("    <Instruction>Call the method through its declared accessible API and assert observable behavior.</Instruction>");
            sb.AppendLine("  </SuggestedTestStrategy>");
            return;
        }

        bool onlyPrivateAccessBlocker =
            method.Accessibility == "Private" &&
            report.Blockers.Count == 1 &&
            report.Blockers[0].StartsWith("Method is 'Private'", StringComparison.Ordinal);

        if (onlyPrivateAccessBlocker)
        {
            sb.AppendLine($"  <SuggestedTestStrategy action=\"Reflection\" testFramework=\"{X(GetTestFrameworkName(_testFramework))}\">");
            sb.AppendLine("    <Instruction>Use reflection only to invoke this synchronous private method; use no invented dependencies.</Instruction>");
            sb.AppendLine("  </SuggestedTestStrategy>");
            return;
        }

        sb.AppendLine($"  <SuggestedTestStrategy action=\"RefactorFirst\" testFramework=\"{X(GetTestFrameworkName(_testFramework))}\">");
        sb.AppendLine($"    <Instruction>The method has blockers that cannot be safely worked around in a test: {X(string.Join(" | ", report.Blockers))}.</Instruction>");
        sb.AppendLine($"    <Fallback>Do not change production code. Emit {X(GetSkipAttribute("requires production-code refactoring"))} and describe the required refactoring only in a comment.</Fallback>");
        sb.AppendLine("  </SuggestedTestStrategy>");
    }

    private string GetSkipAttribute(string reason) => _testFramework switch
    {
        TestFramework.NUnit => $"[Ignore(\"{reason}\")]",
        TestFramework.MSTest => $"[Ignore(\"{reason}\")]",
        TestFramework.xUnit => $"[Fact(Skip = \"{reason}\")]",
        _ => throw new ArgumentOutOfRangeException(
            nameof(_testFramework), _testFramework, "Unsupported test framework.")
    };

    private string BuildProjectContextHint(
        string sourceFilePath,
        string testProjectDirectory,
        TestFramework testFramework,
        MockFramework mockFramework)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<ProjectContext>");

        var sourceProjectPath = FindProjectFile(sourceFilePath);
        if (sourceProjectPath is not null)
        {
            AppendProjectContext(sb, "SourceProject", sourceProjectPath);
        }
        else
        {
            sb.AppendLine("  <SourceProject status=\"not-found\" />");
        }

        var testProjectPath = Directory.Exists(testProjectDirectory)
            ? Directory.GetFiles(testProjectDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;

        if (testProjectPath is not null)
        {
            AppendProjectContext(sb, "TestProject", testProjectPath);
        }
        else
        {
            string derivedRootNamespace = DeriveRootNamespaceFromDirectory(testProjectDirectory);

            sb.AppendLine($"  <TestProject status=\"not-created\" directory=\"{X(testProjectDirectory)}\" template=\"{X(GetTestTemplate(testFramework))}\">");

            if (!string.IsNullOrWhiteSpace(derivedRootNamespace))
                sb.AppendLine($"    <RootNamespace>{X(derivedRootNamespace)}</RootNamespace>");

            sb.AppendLine("    <Note>The project will be created with the selected test framework template. No mocking library or helper package has been verified; do not assume it is available. No <GlobalUsings> are known; include explicit framework using directives.</Note>");
            sb.AppendLine("  </TestProject>");
        }

        sb.AppendLine($"  <SelectedFrameworks test=\"{X(GetTestFrameworkName(testFramework))}\" mocking=\"{X(mockFramework.ToString())}\" />");
        sb.AppendLine("</ProjectContext>");
        return sb.ToString();
    }

    private static void AppendProjectContext(StringBuilder sb, string elementName, string projectPath)
    {
        var doc = XDocument.Load(projectPath);
        string? Val(string name) =>
            doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

        sb.AppendLine($"  <{elementName} status=\"found\" file=\"{X(Path.GetFileName(projectPath))}\">");
        AppendIfSet(sb, "TargetFramework", Val("TargetFramework") ?? Val("TargetFrameworks"), "    ");
        AppendIfSet(sb, "LangVersion", Val("LangVersion"), "    ");
        AppendIfSet(sb, "Nullable", Val("Nullable"), "    ");
        AppendIfSet(sb, "ImplicitUsings", Val("ImplicitUsings"), "    ");
        AppendIfSet(sb, "RootNamespace",
            Val("RootNamespace") ?? DeriveRootNamespaceFromProjectFile(projectPath),
            "    ");

        var packages = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => new
            {
                Name = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "",
                Version = e.Attribute("Version")?.Value
                          ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value
                          ?? ""
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        sb.AppendLine("    <PackageReferences>");
        foreach (var package in packages)
        {
            var version = string.IsNullOrWhiteSpace(package.Version)
                ? "centrally managed or unspecified"
                : package.Version;
            sb.AppendLine($"      <Package id=\"{X(package.Name)}\" version=\"{X(version)}\" />");
        }
        sb.AppendLine("    </PackageReferences>");

        // NEU: <Using Include="..."/>-Items (= globale/implizite Usings) auslesen,
        // damit der LLM weiss, ob "using Xunit;" bereits global verfuegbar ist.
        var globalUsings = doc.Descendants()
            .Where(e => e.Name.LocalName == "Using")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        if (globalUsings.Count > 0)
        {
            sb.AppendLine("    <GlobalUsings>");
            foreach (var u in globalUsings)
                sb.AppendLine($"      <Using>{X(u)}</Using>");
            sb.AppendLine("    </GlobalUsings>");
        }

        var projectReferences = doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? "")
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        if (projectReferences.Count > 0)
        {
            sb.AppendLine("    <ProjectReferences>");
            foreach (var reference in projectReferences)
            {
                var normalizedPath = reference.Replace('\\', Path.DirectorySeparatorChar);
                sb.AppendLine($"      <ProjectReference>{X(Path.GetFileName(normalizedPath))}</ProjectReference>");
            }
            sb.AppendLine("    </ProjectReferences>");
        }

        sb.AppendLine($"  </{elementName}>");
    }

    private static void AppendIfSet(StringBuilder sb, string name, string? value, string indent = "  ")
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"{indent}<{name}>{X(value)}</{name}>");
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

        var (extractedUsings, methodsText) = SplitUsingsFromMethods(methodCode);

        var usings = new List<string> { $"using {GetTestFrameworkNamespace(_testFramework)};" };
        foreach (var u in extractedUsings)
        {
            if (!usings.Contains(u, StringComparer.Ordinal))
                usings.Add(u);
        }

        string usingsBlock = string.Join(Environment.NewLine, usings);

        return $@"{usingsBlock}
namespace {namespaceName}
{{
    public class {testClassName}
    {{
        {methodsText}
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

        foreach (var u in root.Usings)
        {
            sb.AppendLine(u.ToFullString().TrimEnd());
        }

        if (root.Usings.Count > 0)
            sb.AppendLine();

        foreach (var method in methods)
            sb.AppendLine(method.ToFullString());

        return sb.ToString().Trim();
    }

   private static (List<string> Usings, string Methods) SplitUsingsFromMethods(string extractedCode)
    {
        var usings = new List<string>();
        if (string.IsNullOrWhiteSpace(extractedCode))
            return (usings, string.Empty);

        var tree = CSharpSyntaxTree.ParseText(extractedCode);
        var root = tree.GetCompilationUnitRoot();

        foreach (var u in root.Usings)
        {
            var text = u.ToFullString().TrimEnd();
            if (!usings.Contains(text, StringComparer.Ordinal))
                usings.Add(text);
        }

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        if (methods.Count == 0)
            return (usings, extractedCode.Trim());

        var sb = new StringBuilder();
        foreach (var method in methods)
            sb.AppendLine(method.ToFullString());

        return (usings, sb.ToString().Trim());
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

    /// <summary>
    /// Derives a C#-legal root namespace from a test-project directory.
    /// Used when the test project does not exist yet and no .csproj can be read.
    /// Mirrors the SDK-style default: project file name without extension.
    /// </summary>
    private static string DeriveRootNamespaceFromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return string.Empty;

        var name = Path.GetFileName(
            directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return SanitizeNamespace(name);
    }

    /// <summary>
    /// Derives a C#-legal root namespace from a project file path.
    /// Mirrors the SDK-style default: project file name without extension.
    /// </summary>
    private static string DeriveRootNamespaceFromProjectFile(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return string.Empty;

        return SanitizeNamespace(Path.GetFileNameWithoutExtension(projectPath));
    }

    private static string SanitizeNamespace(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return string.Empty;

        var sanitized = Regex.Replace(candidate, @"[^\w\.]", "_");
        if (sanitized.Length == 0)
            return string.Empty;
        if (char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;

        return sanitized;
    }

    private static TestFramework ParseTestFramework(AiTestingConfig config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        if (config.Frameworks is not null &&
            Enum.TryParse(config.Frameworks.TestFramework, ignoreCase: true, out TestFramework framework) &&
            framework != TestFramework.Unknown &&
            Enum.IsDefined(typeof(TestFramework), framework))
        {
            return framework;
        }

        throw new ArgumentException(
            $"Unsupported test framework '{config.Frameworks?.TestFramework}'. Supported values are xunit, nunit, and mstest.",
            nameof(config));
    }

    private static MockFramework ParseMockFramework(AiTestingConfig config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        if (config.Frameworks is not null &&
            Enum.TryParse(config.Frameworks.MockingFramework, ignoreCase: true, out MockFramework framework) &&
            framework != MockFramework.Unknown &&
            Enum.IsDefined(typeof(MockFramework), framework))
        {
            return framework;
        }

        throw new ArgumentException(
            $"Unsupported mocking framework '{config.Frameworks?.MockingFramework}'. Supported values are moq, nsubstitute, and fakeiteasy.",
            nameof(config));
    }

    private static string GetTestTemplate(TestFramework testFramework) => testFramework switch
    {
        TestFramework.NUnit => "nunit",
        TestFramework.MSTest => "mstest",
        TestFramework.xUnit => "xunit",
        _ => throw new ArgumentOutOfRangeException(nameof(testFramework), testFramework, "Unsupported test framework.")
    };

    private static string GetTestFrameworkName(TestFramework testFramework) => testFramework switch
    {
        TestFramework.NUnit => "NUnit",
        TestFramework.MSTest => "MSTest",
        TestFramework.xUnit => "xUnit",
        _ => throw new ArgumentOutOfRangeException(nameof(testFramework), testFramework, "Unsupported test framework.")
    };

    private static string GetTestAttribute(TestFramework testFramework) => testFramework switch
    {
        TestFramework.NUnit => "[Test]",
        TestFramework.MSTest => "[TestMethod]",
        TestFramework.xUnit => "[Fact]",
        _ => throw new ArgumentOutOfRangeException(nameof(testFramework), testFramework, "Unsupported test framework.")
    };

    private static string GetTestFrameworkNamespace(TestFramework testFramework) => testFramework switch
    {
        TestFramework.NUnit => "NUnit.Framework",
        TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting",
        TestFramework.xUnit => "Xunit",
        _ => throw new ArgumentOutOfRangeException(nameof(testFramework), testFramework, "Unsupported test framework.")
    };

}