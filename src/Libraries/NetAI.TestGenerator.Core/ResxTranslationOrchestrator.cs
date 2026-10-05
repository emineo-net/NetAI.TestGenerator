using DotNet10TestGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
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
    private static readonly Regex AiAreaMarkerLineRegex = new(
        @"^[ \t]*//[^\r\n]*AI AREA[^\r\n]*\r?\n" +
        @"|^[ \t]*//[ \t]*=+[ \t]*\r?\n",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TestCodeBlockRegex = new(
        @"```(?:csharp|cs)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly MockFramework _mockFramework;
    private readonly TestCodeBeautifier _testCodeBeautifier = new();
    private readonly TestFramework _testFramework;
    private readonly AiTestingConfig? _config;

    private readonly TestGeneratorService _testGeneratorService;

    /// <summary>Creates an orchestrator for generating tests from source files.</summary>
    public ResxTranslationOrchestrator(HttpClient? httpClient = null)
        : this(TestFramework.xUnit, MockFramework.Unknown, null, httpClient)
    {
    }

    /// <summary>Creates an orchestrator using the configured test and mocking frameworks.</summary>
    public ResxTranslationOrchestrator(AiTestingConfig config, HttpClient? httpClient = null)
        : this(ParseTestFramework(config), ParseMockFramework(config), config, httpClient)
    {
    }

    private ResxTranslationOrchestrator(
        TestFramework testFramework,
        MockFramework mockFramework,
        AiTestingConfig? config,
        HttpClient? httpClient)
    {
        _testGeneratorService = new TestGeneratorService();
        _testFramework = testFramework;
        _mockFramework = mockFramework;
        _config = config;
    }

    /// <summary>Internal helper: return value of BuildSemanticHintAsync.</summary>
    private sealed record SemanticHint(
        string Xml,
        UnitTestSkeletonGenerator.GeneratorResult? Skeleton,
        bool RequiresSta);

    /// <summary>Generates tests for uncovered methods in the first class found in a source file.</summary>
    public async Task<string> ProcessProjectAsync(string sourceFilePath, string testProjectDirectory, Action<string>? logInfo = null,
        Compilation? compilation = null, bool promptOnly = false, string? solutionPath = null, string? projectPath = null)
    {
        IDisposable? msbuildHandle = null;

        try
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                return "Source file empty or missing.";
            }

            string sourceCode;
            using (var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                sourceCode = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var sourceTree =
                compilation?.SyntaxTrees.FirstOrDefault(t => string.Equals(Path.GetFullPath(t.FilePath ?? ""),
                    Path.GetFullPath(sourceFilePath), StringComparison.OrdinalIgnoreCase)) ??
                CSharpSyntaxTree.ParseText(sourceCode, path: sourceFilePath);

            var sourceRoot = sourceTree.GetCompilationUnitRoot();

            var targetClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (targetClass == null)
            {
                return "No class found in source file.";
            }

            var className = targetClass.Identifier.Text;

            var effectiveTestNamespace = ResolveTestNamespace(testProjectDirectory, targetClass);

            var hostProjectDir = promptOnly ? FindProjectDirectory(sourceFilePath) : null;

            BuildLogger.Info(hostProjectDir);

            var testClassName = $"{className}Tests";
            var testFilePath = Path.Combine(testProjectDirectory, $"{testClassName}.cs");

            BuildLogger.Info(testFilePath);

            logInfo?.Invoke($"[NetAI] Starting analysis for class: {className}");

            var existingTestMethods = _testGeneratorService.GetExistingTestMethods(testFilePath);
            var sourceMethods = targetClass.DescendantNodes().OfType<MethodDeclarationSyntax>();

            var localLlmClient = new LocalLlmClient();
            var aiPromptBuilderSimple = new AiPromptBuilderSimple();
            var testProjectManager = new TestProjectManager(testFramework: _testFramework, mockFramework: _mockFramework);

            var compilerService = new TestProjectManagerCompilerService(code => testProjectManager.SetupAndValidateTestAsync(sourceFilePath,
                code, testTemplate: GetTestTemplate(_testFramework), testProjectDirectoryOverride: testProjectDirectory));

            var testCodeProcessor = new TestCodeProcessor(compilerService);

            // ---- Framework profile: single source of truth for skip attributes,
            //      using directives, and mock syntax. Built once per file, before
            //      the semantic-analysis branch, so it is available everywhere
            //      downstream (including the size-limit skip stub in the loop).
            var packages = TestProjectInfo.ReadPackageIds(testProjectDirectory);

            var profile = new TestFrameworkProfile(
                _testFramework,
                _mockFramework,
                _config?.Frameworks?.UseFluentAssertions ?? true,
                _config?.Frameworks?.UseAutoFixture ?? false,
                packages);

            // --- Semantic analysis strategy selection -------------------------------
            RoslynDllTestabilityAnalyzer? semanticAnalyzer = null;
            var effectiveCompilation = compilation;

#if !NETSTANDARD2_0
            bool useMsbuild = !string.IsNullOrWhiteSpace(solutionPath)
                              || !string.IsNullOrWhiteSpace(projectPath);
#else
            var useMsbuild = false;
#endif

            if (useMsbuild)
            {
                semanticAnalyzer = new RoslynDllTestabilityAnalyzer(
                    new AnalyzerOptions { TestFrameworkProfile = profile });

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
                semanticAnalyzer = new RoslynDllTestabilityAnalyzer(
                    new AnalyzerOptions { TestFrameworkProfile = profile });

                logInfo?.Invoke(
                    "[NetAI] Semantic analysis is available via in-memory compilation; AI prompts will include semantic context.");
            }
            else
            {
                logInfo?.Invoke("[NetAI] No compilation or MSBuild path was provided; using syntax-only analysis.");
            }

            var projectContext = BuildProjectContextHint(sourceFilePath, testProjectDirectory, _testFramework, _mockFramework);

            var collectedMethodCodes = new List<string>();

            foreach (var method in sourceMethods)
            {
                var methodName = method.Identifier.Text;

                var testExists = existingTestMethods.Any(t => t.Contains(methodName, StringComparison.OrdinalIgnoreCase));
                if (testExists)
                {
                    logInfo?.Invoke($"[NetAI] Method '{methodName}' is already covered by an existing test. Skipping.");
                    continue;
                }

                // ---- Size gate: skip methods that exceed the configured character limit.
                var maxChars = _config?.MaxMethodChars ?? 25_000;

                if (!IsWithinSizeLimit(method, maxChars, out var methodChars))
                {
                    logInfo?.Invoke(
                        $"[NetAI] Method '{methodName}' is {methodChars:N0} chars (limit: {maxChars:N0}). " +
                        $"Generating skip stub instead of a real test.");

                    var model = effectiveCompilation?.GetSemanticModel(method.SyntaxTree);
                    var methodSymbol = model?.GetDeclaredSymbol(method) as IMethodSymbol;

                    if (methodSymbol is not null)
                    {
                        var skipReason =
                            $"method is {methodChars:N0} chars (limit: {maxChars:N0}). " +
                            $"Refactor into smaller, focused methods.";

                        var skipResult = UnitTestSkeletonGenerator.GenerateFromMethod(
                            methodSymbol,
                            profile,
                            requiresSta: false,
                            strategy: UnitTestSkeletonGenerator.TestStrategy.Skip,
                            refactoringLines: null,
                            skipReason: skipReason);

                        collectedMethodCodes.Add(StripAiAreaMarkers(skipResult.TestSkeleton));
                        existingTestMethods.Add(methodName);
                    }
                    else
                    {
                        logInfo?.Invoke($"[NetAI] Could not resolve '{methodName}' to build a skip stub; skipping entirely.");
                    }

                    continue;
                }

                // ---- Complexity hint (informational, does not skip).
                var complexity = EstimateCyclomaticComplexity(method);
                if (complexity > 20)
                {
                    logInfo?.Invoke(
                        $"[NetAI] Method '{methodName}' has cyclomatic complexity {complexity}. " +
                        $"The generated test will likely cover only the happy path — review manually.");
                }

                logInfo?.Invoke($"[NetAI] Missing test detected for method: {methodName}. Triggering AI generation...");

                
                var trivialModel = effectiveCompilation?.GetSemanticModel(method.SyntaxTree);
                var trivialSymbol = trivialModel?.GetDeclaredSymbol(method) as IMethodSymbol;

                if (trivialModel is not null && trivialSymbol is not null)
                {
                    var trivial = TrivialTestGenerator.TryGenerate(
                        method, trivialSymbol, trivialModel, profile, requiresSta: false);

                    if (trivial.ShouldSkipEntirely)
                    {
                        logInfo?.Invoke($"[NetAI] Method '{methodName}' is marked as not-implemented; skipping entirely.");
                        existingTestMethods.Add(methodName);
                        continue;
                    }

                    if (trivial.TestSnippet is not null)
                    {
                        logInfo?.Invoke($"[NetAI] Method '{methodName}' is trivial; generated template test without LLM.");
                        collectedMethodCodes.Add(trivial.TestSnippet);
                        existingTestMethods.Add(methodName);
                        continue;
                    }
                }


                var classSkeleton = BuildClassSkeleton(targetClass, method);
                BuildLogger.Info(classSkeleton);

                var semanticHint = await BuildSemanticHintAsync(semanticAnalyzer, effectiveCompilation, method, logInfo)
                    .ConfigureAwait(false);

                BuildLogger.Info(semanticHint.Xml);

                var basePrompt = BuildBasePrompt(
                    className, methodName,
                    semanticHint.Xml, projectContext, classSkeleton,
                    semanticHint.Skeleton, semanticHint.RequiresSta);

                BuildLogger.Info(basePrompt);

                if (promptOnly)
                {
                    var promptDirectory = Path.Combine(hostProjectDir ?? Path.GetDirectoryName(sourceFilePath)!, "obj", "netai", "prompts");
                    Directory.CreateDirectory(promptDirectory);
                    var promptPath = Path.Combine(promptDirectory, $"{className}.{methodName}.prompt.md");
                    File.WriteAllText(promptPath, basePrompt, Encoding.UTF8);
                    logInfo?.Invoke($"[NetAI] Prompt with semantic context saved: {promptPath}");
                }

                // ---- System prompt: instruct the model to respect the binding skeleton.
                const string systemPrompt =
                    "You are a C# testing expert. Respond only with runnable C# code and no explanations. " +
                    "If a <BindingSkeleton> block is present, preserve its structure verbatim " +
                    "(usings, namespace, class name, fields, constructor, test attribute) and only fill in the test body.";

                var newTestClassResponse = await localLlmClient.AskAsync(basePrompt, systemPrompt)
                    .ConfigureAwait(false);

                var testMethodCode = ExtractTestClass(newTestClassResponse);
                BuildLogger.Info(testMethodCode);

                if (promptOnly)
                {
                    collectedMethodCodes.Add(StripAiAreaMarkers(testMethodCode));
                    existingTestMethods.Add(methodName);
                    logInfo?.Invoke($"[NetAI] Test draft for '{methodName}' collected; compile validation skipped.");
                    continue;
                }

                TestGenerationResult? result = null;
                var isCompiledSuccessfully = false;

                var aiAttempts = 0;
                var envAttempts = 0;

                const int MaxAiRetries = 1;
                const int MaxEnvRetries = 1;

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

                    var validationClassStructure = PrepareValidationStructure(testClassName,
                        effectiveTestNamespace, testMethodCode);

                    validationClassStructure = await testCodeProcessor
                        .ProcessTestClassAsync(validationClassStructure, _testFramework, _mockFramework).ConfigureAwait(false);

                    result = await testProjectManager.SetupAndValidateTestAsync(sourceFilePath, validationClassStructure,
                            testTemplate: GetTestTemplate(_testFramework), testProjectDirectoryOverride: testProjectDirectory)
                        .ConfigureAwait(false);

                    BuildLogger.Warning(result.CompilerErrors);

                    if (result.IsSuccess)
                    {
                        isCompiledSuccessfully = true;
                        if (!string.IsNullOrEmpty(result.TestClassCode))
                        {
                            testMethodCode = ExtractTestClass(result.TestClassCode!);
                        }

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
                            i.IndexOf("Could not automatically resolve a NuGet package for the namespace(s)",
                                StringComparison.Ordinal) >= 0) == true)
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
                    BuildLogger.Info(errorsText);
                    var codeForRepair = result.TestClassCode ?? validationClassStructure;

                    if (result.CompilerErrors?.Any() == true)
                    {
                        var roslynFixed = await _testCodeBeautifier.TryFixCompilerErrorsAsync(
                            codeForRepair, result.CompilerErrors, _testFramework, _mockFramework).ConfigureAwait(false);

                        if (!string.Equals(roslynFixed, codeForRepair, StringComparison.Ordinal))
                        {
                            BuildLogger.Info(roslynFixed);
                            logInfo?.Invoke("[NetAI] Roslyn automatically added missing using directives; " +
                                            "retrying compilation without AI.");
                            testMethodCode = ExtractTestClass(roslynFixed);
                            continue;
                        }
                    }

                    var errorPrompt = aiPromptBuilderSimple.FixUnittestPromptSimple(errorsText, codeForRepair);
                    BuildLogger.Info(errorPrompt);

                    var repairSystemPrompt = "You are a precise C# compiler assistant. Your only task is to accurately fix " +
                                             "syntax and compilation errors in the provided C# code " +
                                             "and return runnable code without textual explanations.\n";

                    var correctedOutput = await localLlmClient.AskAsync(errorPrompt, repairSystemPrompt).ConfigureAwait(false);
                    testMethodCode = ExtractTestClass(correctedOutput);
                    BuildLogger.Info(testMethodCode);
                }

                BuildLogger.Info("DONE");

                if (isCompiledSuccessfully)
                {
                    collectedMethodCodes.Add(StripAiAreaMarkers(testMethodCode));
                    existingTestMethods.Add(methodName);
                }
                else
                {
                    logInfo?.Invoke(
                        $"[NetAI Warning] Could not generate a compilable test for '{methodName}' after {MaxAiRetries} retries.");
                    if (result?.CompilerErrors != null)
                    {
                        logInfo?.Invoke($"[NetAI] Final Compiler Errors:\n{string.Join("\n", result.CompilerErrors)}");
                    }
                }
            }

            if (collectedMethodCodes.Count > 0)
            {
                var merged = MergeCollectedTestClasses(testClassName, effectiveTestNamespace, collectedMethodCodes);
                File.WriteAllText(testFilePath, merged, Encoding.UTF8);
                logInfo?.Invoke($"[NetAI] Wrote {collectedMethodCodes.Count} test method(s) to {testFilePath}.");
            }

            return "ok";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                msbuildHandle?.Dispose();
            }
            catch
            {
                /* ignore */
            }
        }
    }

    /// <summary>
    ///     Merges multiple complete test-class snippets into a single test class.
    ///     Uses Roslyn's Formatter to reindent everything cleanly; comments and
    ///     doc-comments are preserved, only whitespace is normalized.
    /// </summary>
    private string MergeCollectedTestClasses(
        string testClassName,
        string testNamespace,
        IReadOnlyList<string> classSnippets)
    {
        var usings = new SortedSet<string>(StringComparer.Ordinal);
        var members = new List<MemberDeclarationSyntax>();
        var seenMemberTexts = new HashSet<string>(StringComparer.Ordinal);

        // Ensure the test framework using is always present.
        usings.Add($"using {GetTestFrameworkNamespace(_testFramework)};");

        foreach (var snippet in classSnippets)
        {
            if (string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(snippet);
            var root = tree.GetCompilationUnitRoot();

            foreach (var u in root.Usings)
            {
                var text = u.ToFullString().TrimEnd();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    usings.Add(text);
                }
            }

            var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Any());

            if (classDecl is null)
            {
                continue;
            }

            foreach (var member in classDecl.Members)
            {
                var key = GetMemberKey(member);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (seenMemberTexts.Add(key))
                {
                    members.Add(CleanTrivia(member));
                }
            }
        }

        // Parse the framework usings back into syntax nodes.
        var usingNodes = usings
            .Select(u => CSharpSyntaxTree.ParseText(u).GetCompilationUnitRoot().Usings.FirstOrDefault())
            .Where(u => u is not null)
            .Cast<UsingDirectiveSyntax>()
            .ToArray();

        var classDeclNew = SyntaxFactory.ClassDeclaration(testClassName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(members.ToArray());

        var namespaceDecl = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(testNamespace))
            .AddMembers(classDeclNew);

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usingNodes))
            .AddMembers(namespaceDecl);

        var formatted = Formatter.Format(compilationUnit, new AdhocWorkspace());
        return formatted.ToFullString();
    }


    private static string GetMemberKey(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax m:
                return "M:" + GetMethodKey(m);

            case ConstructorDeclarationSyntax c:
                return "C:" + string.Join(",", c.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""));

            case FieldDeclarationSyntax f:
                return "F:" + string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text));

            case PropertyDeclarationSyntax p:
                return "P:" + p.Identifier.Text;

            case EventFieldDeclarationSyntax e:
                return "E:" + string.Join(",", e.Declaration.Variables.Select(v => v.Identifier.Text));

            default:
                return "O:" + member.NormalizeWhitespace().ToFullString();
        }
    }

    private static string GetMethodKey(MethodDeclarationSyntax method)
    {
        var parts = new List<string>
        {
            method.Identifier.Text,
            (method.TypeParameterList?.Parameters.Count ?? 0).ToString()
        };

        foreach (var p in method.ParameterList.Parameters)
        {
            var refKind = string.Empty;
            foreach (var modifier in p.Modifiers)
            {
                if (modifier.IsKind(SyntaxKind.RefKeyword)
                    || modifier.IsKind(SyntaxKind.OutKeyword)
                    || modifier.IsKind(SyntaxKind.InKeyword))
                {
                    refKind = modifier.ValueText;
                    break;
                }
            }

            parts.Add((p.Type?.WithoutTrivia().ToString() ?? string.Empty) + ":" + refKind);
        }

        return string.Join("|", parts);
    }

    /// <summary>
    ///     Removes whitespace-only leading and trailing trivia from a member so
    ///     that Roslyn's Formatter can reindent it cleanly. Comments and doc
    ///     comments are preserved; a single-line comment always gets an explicit
    ///     end-of-line inserted after it, otherwise consecutive comments would
    ///     collapse onto one line.
    /// </summary>
    private static MemberDeclarationSyntax CleanTrivia(MemberDeclarationSyntax member)
    {
        var originalLeading = member.GetLeadingTrivia();

        var preserved = new List<SyntaxTrivia>();
        foreach (var trivia in originalLeading)
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                preserved.Add(trivia);

                // Single-line comments do not carry their own newline in Roslyn;
                // we must re-add it so the next comment or member starts on a new line.
                if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                {
                    preserved.Add(SyntaxFactory.EndOfLine("\n"));
                }
            }
            else if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                // Preserve explicit blank lines between comments so that
                // intentional spacing survives the merge.
                if (preserved.Count > 0 && preserved[^1].IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    preserved.Add(trivia);
                }
            }
        }

        return member
            .WithLeadingTrivia(SyntaxFactory.TriviaList(preserved))
            .WithTrailingTrivia();
    }

    // ---------------------------------------------------------------- prompt building

    private string BuildBasePrompt(
        string className,
        string methodName,
        string semanticHintXml,
        string projectContext,
        string classSkeleton,
        UnitTestSkeletonGenerator.GeneratorResult? skeleton,
        bool requiresSta)
    {
        var frameworkName = GetTestFrameworkName(_testFramework);
        var testAttribute = GetTestAttribute(_testFramework);
        var skipAttributeTemplate = GetSkipAttribute("<reason>");
        var mockFrameworkInstruction = _mockFramework == MockFramework.Unknown
            ? "Do not introduce a mocking library unless it is listed in <TestProject>."
            : $"When mocking is needed, use {_mockFramework} only if it is listed in <TestProject>.";

        var verbose = _config?.Frameworks?.VerbosePrompt ?? true;

        var sb = new StringBuilder();

        // --- Role & goal ---
        sb.AppendLine($"You are a .NET testing expert working with {frameworkName}.");
        sb.AppendLine($"Generate the test for method '{methodName}' in class '{className}' according to <SuggestedTestStrategy>.");
        sb.AppendLine($"- If the strategy is 'Generate' or 'Direct', produce one test method using {testAttribute}.");
        sb.AppendLine(
            "- If the strategy is 'Skip', produce one test method with the framework's skip/ignore attribute " +
            $"(e.g. {skipAttributeTemplate}); put the body in a comment only.");
        sb.AppendLine(
            "- If the strategy is 'RefactorFirst', produce a Skip test AND list the required source refactorings as comments " +
            "above the test. When the strategy includes a <SuggestedRefactoringPattern>, mirror it in the comments.");
        sb.AppendLine(
            "- If the strategy is 'Reflection', produce one test method that invokes the private method via reflection. " +
            "This strategy explicitly overrides the 'no reflection' rule below and applies only when the strategy says so.");
        sb.AppendLine();

        // --- Source of truth ---
        sb.AppendLine(
            "Use <SemanticAnalysis> as the source of truth for method and dependency facts, and follow <SuggestedTestStrategy> exactly.");
        sb.AppendLine(
            "Do not use reflection, dynamic invocation, or workaround code for private/static/async-void members, " +
            "unless <SuggestedTestStrategy> explicitly instructs it (see 'Reflection' strategy).");
        sb.AppendLine();

        // --- Test project rules ---
        sb.AppendLine("Rules for the test project:");
        sb.AppendLine("- You MAY create a new test class in the test project (naming: <ClassUnderTest>Tests).");
        sb.AppendLine("- Do NOT invent source types, members, project references, or NuGet packages.");
        sb.AppendLine(
            "- You MAY use namespaces from referenced assemblies, from packages listed in <ProjectContext>, " +
            "and from `using` directives shown in <SourceCode>. Do NOT invent new namespaces that are not derivable from these sources.");
        sb.AppendLine("- Do NOT invent types that are not present in <ProjectContext> or <SemanticAnalysis>.");
        sb.AppendLine($"- {mockFrameworkInstruction}");
        sb.AppendLine("- Use the selected test framework from <SelectedFrameworks>; the test project uses that framework's template.");
        sb.AppendLine("- Use a mocking library or helper only if it is listed in <TestProject>.");
        sb.AppendLine(
            "- Keep source-code refactoring advice separate from the generated test; do not modify or assume changes to the source project.");
        sb.AppendLine(
            "- If the method under test returns Task or Task<T>, make the test method async Task. " +
            "Do NOT make the test method async for 'async void' methods; those are covered by <SuggestedTestStrategy> (Skip/RefactorFirst).");
        sb.AppendLine(
            "- If <SemanticAnalysis> contains <StaRequirement required=\"true\" />, the test must run on an STA thread. " +
            $"If the test project lists a compatible STA helper package (e.g. \"Xunit.StaFact\" for xUnit) under <PackageReferences>, " +
            $"use its attribute (e.g. [StaFact]) instead of {testAttribute}. " +
            $"Otherwise keep {testAttribute} and add a comment noting the STA requirement.");
        sb.AppendLine(
            "- Name the test method following the pattern `MethodName_Scenario_ExpectedBehavior` " +
            "(e.g. `ProcessOrder_EmptyCart_ThrowsInvalidOperationException`). " +
            "For a Skip/RefactorFirst strategy, prefer a name like `MethodName_IsNotDirectlyTestable` or a similarly descriptive name.");
        sb.AppendLine(
            "- The test project has <Nullable>enable</Nullable>. The generated test code must be nullable-correct: " +
            "no non-nullable fields left uninitialized, no `null` assigned to non-nullable references, " +
            "and no null-forgiving `!` operator unless the source already uses it in the same member.");
        sb.AppendLine();

        // --- Output format ---
        sb.AppendLine("Output format:");
        sb.AppendLine("- Return ONLY compilable C# code (no explanations, no prose, no TODO markers outside comments).");
        sb.AppendLine("- \"Compilable C# code\" refers to the generated TEST code, assuming the source project\r\n  compiles as-is. Source compilation errors reported by the analyzer are host artifacts\r\n  and do not affect this assumption.");
        sb.AppendLine(
            $"- Include \"using {GetTestFrameworkNamespace(_testFramework)};\" at the top of the generated code UNLESS the test project's <ProjectContext> already lists that namespace under <GlobalUsings>.");
        sb.AppendLine("- Use top-level usings consistent with ImplicitUsings/Nullable settings from <ProjectContext>.");
        sb.AppendLine("- Use the test project's <RootNamespace> from <ProjectContext> verbatim when present.");
        sb.AppendLine(
            "- If <RootNamespace> is absent, derive the namespace from the test project file name (without the .csproj extension). Do not invent any other namespace.");
        sb.AppendLine($"- Include {testAttribute} (or the framework-specific attribute, including STA variants) exactly once.");
        sb.AppendLine();

        // --- Binding Skeleton ---
        if (skeleton is { } sk && sk.Mode != UnitTestSkeletonGenerator.SkeletonMode.Fallback)
        {
            sb.AppendLine("=== BINDING TEST SKELETON ===");
            sb.AppendLine("The skeleton below is the authoritative structure. You MUST:");
            sb.AppendLine("- Preserve the using directives, namespace, class name, field names, constructor and test attribute EXACTLY as shown.");
            sb.AppendLine("- Fill in ONLY the content between the 'AI AREA' markers.");
            sb.AppendLine("- Do NOT add new fields, mocks, or helper methods unless the strategy explicitly requires it.");
            sb.AppendLine("- Do NOT replace the test attribute with a different one.");
            sb.AppendLine();

            if (verbose)
            {
                switch (sk.Mode)
                {
                    case UnitTestSkeletonGenerator.SkeletonMode.ConstructorInjection:
                        sb.AppendLine("Mode: ConstructorInjection.");
                        sb.AppendLine($"- The SUT is `{sk.SutTypeName}` and receives mocked dependencies via its constructor.");
                        sb.AppendLine("- The mocks are already declared as fields and passed to the SUT constructor.");
                        sb.AppendLine("- Use the exact mock-setup syntax shown in the AiPromptContext above (framework-specific).");
                        break;

                    case UnitTestSkeletonGenerator.SkeletonMode.Parameterless:
                        sb.AppendLine("Mode: Parameterless.");
                        sb.AppendLine($"- `{sk.SutTypeName}` has no constructor parameters. Do NOT create mocks.");
                        sb.AppendLine("- Only use the fields that already exist in the skeleton.");
                        break;

                    case UnitTestSkeletonGenerator.SkeletonMode.StaticOrAbstract:
                        sb.AppendLine("Mode: StaticOrAbstract.");
                        sb.AppendLine($"- `{sk.SutTypeName}` cannot be instantiated. Call `{sk.SutTypeName}.{sk.TargetMethodName}(...)` directly.");
                        sb.AppendLine("- Do NOT use `new` or `_sut`.");
                        break;

                    case UnitTestSkeletonGenerator.SkeletonMode.RefactorFirst:
                        sb.AppendLine("Mode: RefactorFirst.");
                        sb.AppendLine("- The method cannot be tested as-is. The skeleton already contains a Skip attribute.");
                        sb.AppendLine("- Keep the Skip attribute verbatim.");
                        sb.AppendLine("- Inside the AI AREA, list the required source refactorings as comments (no real test body).");
                        break;

                    case UnitTestSkeletonGenerator.SkeletonMode.Skip:
                        sb.AppendLine("Mode: Skip.");
                        sb.AppendLine("- The method must be skipped. The skeleton already contains a Skip attribute.");
                        sb.AppendLine("- Keep the Skip attribute verbatim.");
                        sb.AppendLine("- Inside the AI AREA, explain briefly why the test is skipped (no real test body).");
                        break;
                }

                if (requiresSta)
                {
                    sb.AppendLine("- This test is STA-bound. Use the test attribute already present in the skeleton verbatim.");
                }

                sb.AppendLine();
            }

            sb.AppendLine("<BindingSkeleton>");
            sb.AppendLine(sk.TestSkeleton);
            sb.AppendLine("</BindingSkeleton>");
            sb.AppendLine();
        }

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

    private async Task<SemanticHint> BuildSemanticHintAsync(
        RoslynDllTestabilityAnalyzer? analyzer,
        Compilation? compilation,
        MethodDeclarationSyntax methodDeclaration,
        Action<string>? logInfo)
    {
        if (analyzer == null || compilation == null)
        {
            return new SemanticHint(string.Empty, null, false);
        }

        var methodName = methodDeclaration.Identifier.Text;

        try
        {
            var report = await analyzer.AnalyzeFromCompilationAsync(compilation, methodDeclaration).ConfigureAwait(false);

            if (report.TestSkeleton is { } skel)
            {
                logInfo?.Invoke(
                    $"[NetAI] Skeleton mode for '{methodName}': {skel.Mode}, " +
                    $"STA={skel.RequiresSta}, class={skel.TestClassName}");
            }

            if (!report.IsDirectlyTestable)
            {
                logInfo?.Invoke($"[NetAI] Blockers for '{methodName}': {string.Join(" | ", report.Blockers)}");
            }

            logInfo?.Invoke($"[NetAI] Semantic analysis for '{methodName}': {report.Verdict}");

            var filePath = methodDeclaration.SyntaxTree.FilePath ?? methodName;
            var xml = FormatReportAsXml(report, filePath, methodName);

            return new SemanticHint(xml, report.TestSkeleton, report.RequiresSta);
        }
        catch (InvalidOperationException ex)
        {
            logInfo?.Invoke($"[NetAI] Skipped semantic analysis for '{methodName}': {ex.Message}");
            return new SemanticHint(string.Empty, null, false);
        }
        catch (Exception ex)
        {
            logInfo?.Invoke($"[NetAI] Semantic analysis for '{methodName}' failed: {ex.Message}");
            return new SemanticHint(string.Empty, null, false);
        }
    }

    private string FormatReportAsXml(TestabilityReport report, string sourceFilePath, string methodName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<SemanticAnalysis>");
        sb.AppendLine(
            $"  <Document name=\"{X(Path.GetFileName(sourceFilePath))}\" method=\"{X(methodName)}\" generatedAt=\"{report.GeneratedAt:O}\" />");

        if (report.Method is { } m)
        {
            sb.AppendLine($"  <Method name=\"{X(m.Name)}\" signature=\"{X(m.Signature)}\" accessibility=\"{X(m.Accessibility)}\" " +
                          $"returnType=\"{X(m.ReturnType)}\" isStatic=\"{m.IsStatic}\" isAsync=\"{m.IsAsync}\" " +
                          $"returnsVoid=\"{m.ReturnsVoid}\" isAsyncVoid=\"{m.IsAsyncVoid}\" isVirtual=\"{m.IsVirtual}\" " +
                          $"returnsTask=\"{m.ReturnsTask}\" hasCancellationToken=\"{m.HasCancellationToken}\" />");

            if (m.GenericParameters.Count > 0)
            {
                sb.AppendLine($"    <GenericParameters>{X(string.Join(", ", m.GenericParameters))}</GenericParameters>");
            }

            if (m.Attributes.Count > 0)
            {
                sb.AppendLine("    <Attributes>");
                foreach (var a in m.Attributes)
                {
                    sb.AppendLine($"      <Attribute>{X(a)}</Attribute>");
                }
                sb.AppendLine("    </Attributes>");
            }

            if (m.ThrownExceptions.Count > 0)
            {
                sb.AppendLine("    <ThrownExceptions>");
                foreach (var t in m.ThrownExceptions)
                {
                    sb.AppendLine($"      <Exception>{X(t)}</Exception>");
                }
                sb.AppendLine("    </ThrownExceptions>");
            }

            if (m.ContainingType is { } ct)
            {
                sb.AppendLine($"  <ContainingType fullName=\"{X(ct.FullName)}\" kind=\"{X(ct.Kind)}\" " +
                              $"accessibility=\"{X(ct.Accessibility)}\" isStatic=\"{ct.IsStatic}\" " +
                              $"isSealed=\"{ct.IsSealed}\" isAbstract=\"{ct.IsAbstract}\" mockable=\"{X(ct.Mockable)}\" />");
                if (!string.IsNullOrEmpty(ct.BaseType))
                {
                    sb.AppendLine($"    <BaseType>{X(ct.BaseType)}</BaseType>");
                }
                if (ct.Interfaces.Count > 0)
                {
                    sb.AppendLine($"    <Interfaces>{X(string.Join(", ", ct.Interfaces))}</Interfaces>");
                }
                if (ct.AllBaseTypes is { Count: > 0 })
                {
                    sb.AppendLine($"    <BaseTypes>{X(string.Join(" -> ", ct.AllBaseTypes))}</BaseTypes>");
                }
                if (ct.Constructors.Count > 0)
                {
                    sb.AppendLine($"    <PublicCtors>{X(string.Join(" | ", ct.Constructors))}</PublicCtors>");
                }
                else
                {
                    sb.AppendLine("    <PublicCtors>none</PublicCtors>");
                }
            }
        }

        // NOTE: <TestSkeleton> block removed intentionally. The skeleton is
        // injected into the prompt as <BindingSkeleton> by BuildBasePrompt.
        // Only the STA fact remains here, because it is a constraint, not a template.
        if (report.RequiresSta)
        {
            sb.AppendLine("  <StaRequirement required=\"true\" framework=\"WPF\" " +
                          "note=\"WPF UI types require an STA thread; use an STA-aware test attribute when available.\" />");
        }

        sb.AppendLine($"  <Testability verdict=\"{X(report.Verdict)}\" isDirectlyTestable=\"{report.IsDirectlyTestable}\" />");

        if (report.Blockers.Count > 0)
        {
            sb.AppendLine("  <Blockers>");
            foreach (var b in report.Blockers)
            {
                sb.AppendLine($"    <Blocker>{X(b)}</Blocker>");
            }
            sb.AppendLine("  </Blockers>");
        }

        var relevant = report.ReferencedTypes?.Where(t =>
            t.UsedStatically || t.DependencyKind == DependencyKind.Interface || t.DependencyKind == DependencyKind.AbstractClass ||
            !t.Namespace.StartsWith("System", StringComparison.Ordinal)).ToList() ?? new List<TypeFact>();

        if (relevant.Count > 0)
        {
            sb.AppendLine("  <Dependencies>");
            foreach (var t in relevant)
            {
                sb.AppendLine($"    <Dependency type=\"{X(t.FullName)}\" kind=\"{t.DependencyKind}\" " +
                              $"mockable=\"{X(t.Mockable)}\" usedStatically=\"{t.UsedStatically}\" " +
                              $"usages=\"{t.Usages}\" />");

                if (t.Constructors.Count > 0)
                {
                    var ctors = t.Constructors.Take(3).ToList();
                    sb.AppendLine($"      <PublicCtors>{X(string.Join(" | ", ctors))}</PublicCtors>");
                }

                if (t.Interfaces.Count > 0)
                {
                    var interfaces = t.Interfaces.Take(5).ToList();
                    sb.AppendLine($"      <Interfaces>{X(string.Join(", ", interfaces))}</Interfaces>");
                }

                if (!string.IsNullOrEmpty(t.RecommendedAbstraction))
                {
                    sb.AppendLine($"      <RecommendedAbstraction>{X(t.RecommendedAbstraction)}</RecommendedAbstraction>");
                }

                if (!string.IsNullOrEmpty(t.RecommendedAbstractionPackage))
                {
                    sb.AppendLine($"      <RecommendedAbstractionPackage>{X(t.RecommendedAbstractionPackage)}</RecommendedAbstractionPackage>");
                }

                if (!string.IsNullOrEmpty(t.RecommendationReason))
                {
                    sb.AppendLine($"      <Reason>{X(t.RecommendationReason)}</Reason>");
                }
            }
            sb.AppendLine("  </Dependencies>");
        }

        if (report.AnalyzedCallGraph.Count > 1)
        {
            sb.AppendLine("  <CallGraph>");
            foreach (var c in report.AnalyzedCallGraph)
            {
                sb.AppendLine($"    <Call>{X(c)}</Call>");
            }
            sb.AppendLine("  </CallGraph>");
        }

        sb.AppendLine("  <Recommendations>");
        sb.AppendLine("    <SourceRefactoring>");
        foreach (var recommendation in report.SourceRefactoringRecommendations)
        {
            sb.AppendLine($"      <Recommendation>{X(recommendation)}</Recommendation>");
        }
        sb.AppendLine("    </SourceRefactoring>");
        sb.AppendLine("    <TestStrategy>");
        foreach (var recommendation in report.TestStrategyRecommendations)
        {
            sb.AppendLine($"      <Recommendation>{X(recommendation)}</Recommendation>");
        }
        sb.AppendLine("    </TestStrategy>");
        sb.AppendLine("  </Recommendations>");

        AppendSuggestedTestStrategy(sb, report);

        // Source compilation errors are intentionally NOT emitted to the prompt —
        // they are analyzer-host artifacts and only confuse small models.

        sb.AppendLine("</SemanticAnalysis>");
        return sb.ToString();
    }

    private void AppendSuggestedTestStrategy(StringBuilder sb, TestabilityReport report)
    {
        var method = report.Method;
        var frameworkName = X(GetTestFrameworkName(_testFramework));

        switch (report.Strategy)
        {
            case UnitTestSkeletonGenerator.TestStrategy.RefactorFirst:
                {
                    if (method.IsAsyncVoid)
                    {
                        sb.AppendLine($"  <SuggestedTestStrategy action=\"RefactorFirst\" testFramework=\"{frameworkName}\">");
                        sb.AppendLine("    <Instruction>Split the async void handler into a thin UI shim and a testable async Task, then write the test against the Task.</Instruction>");
                        sb.AppendLine("    <Fallback>Do not change production code. Keep the Skip attribute from the binding skeleton verbatim and describe the required refactoring only in a comment.</Fallback>");
                        sb.AppendLine("    <SuggestedRefactoringPattern>");
                        foreach (var line in BuildAsyncVoidRefactoringPattern(method))
                        {
                            sb.AppendLine($"      {X(line)}");
                        }
                        sb.AppendLine("    </SuggestedRefactoringPattern>");
                        sb.AppendLine("    <Constraint>Do not use reflection. Do not invoke the handler directly. Do not perform real static I/O.</Constraint>");
                        sb.AppendLine("  </SuggestedTestStrategy>");
                    }
                    else
                    {
                        sb.AppendLine($"  <SuggestedTestStrategy action=\"RefactorFirst\" testFramework=\"{frameworkName}\">");
                        sb.AppendLine($"    <Instruction>The method has blockers that cannot be safely worked around in a test: {X(string.Join(" | ", report.Blockers))}.</Instruction>");
                        sb.AppendLine("    <Fallback>Do not change production code. Keep the Skip attribute from the binding skeleton verbatim and describe the required refactoring only in a comment.</Fallback>");
                        sb.AppendLine("  </SuggestedTestStrategy>");
                    }
                    return;
                }

            case UnitTestSkeletonGenerator.TestStrategy.Reflection:
                sb.AppendLine($"  <SuggestedTestStrategy action=\"Reflection\" testFramework=\"{frameworkName}\">");
                sb.AppendLine("    <Instruction>Use reflection only to invoke this synchronous private method; use no invented dependencies.</Instruction>");
                sb.AppendLine("  </SuggestedTestStrategy>");
                return;

            case UnitTestSkeletonGenerator.TestStrategy.Skip:
                sb.AppendLine($"  <SuggestedTestStrategy action=\"Skip\" testFramework=\"{frameworkName}\">");
                sb.AppendLine("    <Instruction>Skip this method and explain why in a comment.</Instruction>");
                sb.AppendLine("  </SuggestedTestStrategy>");
                return;

            default: // Direct
                sb.AppendLine($"  <SuggestedTestStrategy action=\"Direct\" testFramework=\"{frameworkName}\">");
                sb.AppendLine("    <Instruction>Call the method through its declared accessible API and assert observable behavior.</Instruction>");
                sb.AppendLine("  </SuggestedTestStrategy>");
                return;
        }
    }

    private static IReadOnlyList<string> BuildAsyncVoidRefactoringPattern(MethodFact method)
    {
        var asyncName = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name + "Core"
            : method.Name + "Async";

        return new[]
        {
            "// BEFORE:",
            $"//   private async void {method.Name}(object sender, RoutedEventArgs e)",
            "//   { /* original async body */ }",
            "//",
            "// AFTER:",
            $"//   private async void {method.Name}(object sender, RoutedEventArgs e)",
            $"//       => await {asyncName}();",
            "//",
            $"//   private async Task {asyncName}()",
            "//   { /* original async body, now awaitable & testable */ }",
            "//",
            "// Rationale: the shim stays UI-bound by design and is excluded from unit tests;",
            $"// the {asyncName} method carries all logic and is fully unit-testable."
        };
    }

    private string GetSkipAttribute(string reason)
    {
        return _testFramework switch
        {
            TestFramework.NUnit => $"[Ignore(\"{reason}\")]",
            TestFramework.MSTest => $"[Ignore(\"{reason}\")]",
            TestFramework.xUnit => $"[Fact(Skip = \"{reason}\")]",
            _ => throw new ArgumentOutOfRangeException(nameof(_testFramework), _testFramework, "Unsupported test framework.")
        };
    }

    private string BuildProjectContextHint(string sourceFilePath, string testProjectDirectory, TestFramework testFramework,
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
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;

        if (testProjectPath is not null)
        {
            AppendProjectContext(sb, "TestProject", testProjectPath);
        }
        else
        {
            var derivedRootNamespace = DeriveRootNamespaceFromDirectory(testProjectDirectory);

            sb.AppendLine(
                $"  <TestProject status=\"not-created\" directory=\"{X(testProjectDirectory)}\" template=\"{X(GetTestTemplate(testFramework))}\">");

            if (!string.IsNullOrWhiteSpace(derivedRootNamespace))
            {
                sb.AppendLine($"    <RootNamespace>{X(derivedRootNamespace)}</RootNamespace>");
            }

            sb.AppendLine(
                "    <Note>The project will be created with the selected test framework template. No mocking library or helper package has been verified; do not assume it is available. No <GlobalUsings> are known; include explicit framework using directives.</Note>");
            sb.AppendLine("  </TestProject>");
        }

        sb.AppendLine(
            $"  <SelectedFrameworks test=\"{X(GetTestFrameworkName(testFramework))}\" mocking=\"{X(mockFramework.ToString())}\" />");
        sb.AppendLine("</ProjectContext>");
        return sb.ToString();
    }

    private static void AppendProjectContext(StringBuilder sb, string elementName, string projectPath)
    {
        var doc = XDocument.Load(projectPath);

        string? Val(string name)
        {
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();
        }

        sb.AppendLine($"  <{elementName} status=\"found\" file=\"{X(Path.GetFileName(projectPath))}\">");
        AppendIfSet(sb, "TargetFramework", Val("TargetFramework") ?? Val("TargetFrameworks"), "    ");
        AppendIfSet(sb, "LangVersion", Val("LangVersion"), "    ");
        AppendIfSet(sb, "Nullable", Val("Nullable"), "    ");
        AppendIfSet(sb, "ImplicitUsings", Val("ImplicitUsings"), "    ");
        AppendIfSet(sb, "RootNamespace", Val("RootNamespace") ?? DeriveRootNamespaceFromProjectFile(projectPath), "    ");

        var packages = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e =>
            new
            {
                Name = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "",
                Version = e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? ""
            }).Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

        sb.AppendLine("    <PackageReferences>");
        foreach (var package in packages)
        {
            var version = string.IsNullOrWhiteSpace(package.Version) ? "centrally managed or unspecified" : package.Version;
            sb.AppendLine($"      <Package id=\"{X(package.Name)}\" version=\"{X(version)}\" />");
        }
        sb.AppendLine("    </PackageReferences>");

        var globalUsings = doc.Descendants().Where(e => e.Name.LocalName == "Using").Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

        if (globalUsings.Count > 0)
        {
            sb.AppendLine("    <GlobalUsings>");
            foreach (var u in globalUsings)
            {
                sb.AppendLine($"      <Using>{X(u)}</Using>");
            }
            sb.AppendLine("    </GlobalUsings>");
        }

        var projectReferences = doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? "").Where(path => !string.IsNullOrWhiteSpace(path)).ToList();

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
        {
            sb.AppendLine($"{indent}<{name}>{X(value)}</{name}>");
        }
    }

    private static string X(string? s)
    {
        return string.IsNullOrEmpty(s)
            ? ""
            : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    // ---------------------------------------------------------------- validation structure

    /// <summary>
    ///     Wraps the AI response in a fully-formed test class ready for compilation.
    ///     If the response already contains a complete class (with fields, constructor
    ///     and a test method), that class's members are preserved verbatim; only the
    ///     class name and namespace are rewritten. Otherwise the legacy behavior applies
    ///     (extract the test method and wrap it in a fresh class).
    /// </summary>
    private string PrepareValidationStructure(string testClassName, string? testNamespaceName, string methodCode)
    {
        var namespaceName = string.IsNullOrWhiteSpace(testNamespaceName)
            ? "NetAI.Generated.Tests"
            : testNamespaceName!;

        if (!namespaceName.EndsWith(".Tests", StringComparison.Ordinal))
        {
            namespaceName += ".Tests";
        }

        if (string.IsNullOrWhiteSpace(methodCode))
        {
            throw new InvalidOperationException("Generated code does not contain a test method.");
        }

        var generatedRoot = CSharpSyntaxTree.ParseText(methodCode).GetCompilationUnitRoot();

        List<MemberDeclarationSyntax> members;

        var incomingClass = generatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(HasTestAttribute));

        if (incomingClass is not null)
        {
            members = incomingClass.Members.Cast<MemberDeclarationSyntax>().ToList();
        }
        else
        {
            var methods = generatedRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();

            if (methods.Length == 0)
            {
                var classMembers = methodCode;
                foreach (var usingDirective in generatedRoot.Usings.OrderByDescending(directive => directive.SpanStart))
                {
                    classMembers = classMembers.Remove(usingDirective.SpanStart, usingDirective.Span.Length);
                }

                var wrappedRoot = CSharpSyntaxTree.ParseText($"class GeneratedTestContainer {{ {classMembers} }}").GetCompilationUnitRoot();
                methods = wrappedRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
            }

            if (methods.Length == 0)
            {
                throw new InvalidOperationException("Generated code does not contain a test method.");
            }

            members = methods.Cast<MemberDeclarationSyntax>().ToList();
        }

        var usings = new List<UsingDirectiveSyntax>
        {
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetTestFrameworkNamespace(_testFramework)))
        };

        var usingKeys = new HashSet<string>(usings.Select(GetUsingKey), StringComparer.Ordinal);
        foreach (var usingDirective in generatedRoot.Usings)
        {
            var normalizedUsing = usingDirective.WithoutTrivia();
            if (usingKeys.Add(GetUsingKey(normalizedUsing)))
            {
                usings.Add(normalizedUsing);
            }
        }

        var generatedClass = SyntaxFactory.ClassDeclaration(testClassName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(members.ToArray());

        var generatedNamespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName))
            .AddMembers(generatedClass);

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usings))
            .AddMembers(generatedNamespace);

        return Formatter.Format(compilationUnit, new AdhocWorkspace()).ToFullString();
    }

    private static string GetUsingKey(UsingDirectiveSyntax usingDirective)
    {
        var normalized = usingDirective.WithoutTrivia();
        return string.Join("|", normalized.GlobalKeyword.RawKind, normalized.StaticKeyword.RawKind, normalized.Alias?.Name.ToString(),
            normalized.Name?.ToString());
    }

    // ---------------------------------------------------------------- source skeleton

    private static string BuildClassSkeleton(ClassDeclarationSyntax targetClass, MethodDeclarationSyntax targetMethod,
        bool includeProperties = true, bool includeConstructors = true, bool includeRecursiveHelpers = true)
    {
        var sb = new StringBuilder();

        var usings = targetClass.SyntaxTree.GetCompilationUnitRoot().Usings;
        if (usings.Any())
        {
            foreach (var u in usings)
            {
                sb.AppendLine(u.ToFullString().TrimEnd());
            }
            sb.AppendLine();
        }

        foreach (var field in targetClass.Members.OfType<FieldDeclarationSyntax>())
        {
            sb.AppendLine(field.ToFullString().TrimEnd());
        }

        if (includeProperties)
        {
            foreach (var prop in targetClass.Members.OfType<PropertyDeclarationSyntax>())
            {
                sb.AppendLine(prop.ToFullString().TrimEnd());
            }
        }

        if (includeConstructors)
        {
            foreach (var ctor in targetClass.Members.OfType<ConstructorDeclarationSyntax>())
            {
                sb.AppendLine(ctor.ToFullString().TrimEnd());
            }
        }

        sb.AppendLine();
        sb.AppendLine(targetMethod.ToFullString().TrimEnd());

        var collectedHelpers = new HashSet<string>(StringComparer.Ordinal);
        var helperSb = new StringBuilder();

        void CollectHelpers(MethodDeclarationSyntax method)
        {
            foreach (var calledName in GetCalledMethodNames(method))
            {
                if (!collectedHelpers.Add(calledName))
                {
                    continue;
                }

                var helper = targetClass.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == calledName);

                if (helper is null)
                {
                    continue;
                }

                helperSb.AppendLine(helper.ToFullString().TrimEnd());

                if (includeRecursiveHelpers)
                {
                    CollectHelpers(helper);
                }
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

    // ---------------------------------------------------------------- response extraction

    /// <summary>
    ///     Extracts the meaningful part of an AI response.
    ///     If the response contains a full test class (with fields, ctor, and a test
    ///     method), the complete class is returned — this preserves mock fields and
    ///     the constructor so they survive into the final written file.
    ///     If only method(s) are returned, the legacy behavior applies: usings + the
    ///     first method carrying a recognized test attribute.
    /// </summary>
    public static string ExtractTestClass(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
        {
            return string.Empty;
        }

        var match = TestCodeBlockRegex.Match(aiResponse);
        var rawCode = match.Success ? match.Groups[1].Value : aiResponse;

        var tree = CSharpSyntaxTree.ParseText(rawCode);
        var root = tree.GetCompilationUnitRoot();

        var classWithTest = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(HasTestAttribute));

        if (classWithTest is not null)
        {
            var sbClass = new StringBuilder();

            foreach (var u in root.Usings)
            {
                sbClass.AppendLine(u.ToFullString().TrimEnd());
            }

            if (root.Usings.Count > 0)
            {
                sbClass.AppendLine();
            }

            var namespaceDecl = classWithTest.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

            if (namespaceDecl is FileScopedNamespaceDeclarationSyntax fileScoped)
            {
                sbClass.AppendLine($"namespace {fileScoped.Name};");
                sbClass.AppendLine();
                sbClass.AppendLine(classWithTest.ToFullString().TrimEnd());
            }
            else if (namespaceDecl is NamespaceDeclarationSyntax block)
            {
                sbClass.AppendLine($"namespace {block.Name}");
                sbClass.AppendLine("{");
                sbClass.AppendLine(classWithTest.ToFullString().TrimEnd());
                sbClass.AppendLine("}");
            }
            else
            {
                sbClass.AppendLine(classWithTest.ToFullString().TrimEnd());
            }

            return sbClass.ToString().Trim();
        }

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        if (methods.Count == 0)
        {
            return string.Empty;
        }

        var chosen = methods.FirstOrDefault(HasTestAttribute) ?? methods[0];

        var sb = new StringBuilder();

        foreach (var u in root.Usings)
        {
            sb.AppendLine(u.ToFullString().TrimEnd());
        }

        if (root.Usings.Count > 0)
        {
            sb.AppendLine();
        }

        sb.AppendLine(chosen.ToFullString());

        return sb.ToString().Trim();
    }

    /// <summary>
    ///     True when the method carries a test attribute recognized across the
    ///     supported frameworks (xUnit, NUnit, MSTest) and their STA variants.
    /// </summary>
    private static bool HasTestAttribute(MethodDeclarationSyntax method)
    {
        foreach (var attributeList in method.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var name = attribute.Name.ToString();

                if (name.EndsWith("Fact", StringComparison.Ordinal) ||
                    name.EndsWith("Theory", StringComparison.Ordinal) ||
                    name.EndsWith("Test", StringComparison.Ordinal) ||
                    name.EndsWith("TestCase", StringComparison.Ordinal) ||
                    name.EndsWith("TestMethod", StringComparison.Ordinal) ||
                    name.EndsWith("DataTestMethod", StringComparison.Ordinal) ||
                    name.EndsWith("StaFact", StringComparison.Ordinal) ||
                    name.EndsWith("STATestMethod", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- file / namespace helpers

    private static string? FindProjectDirectory(string filePath)
    {
        try
        {
            var currentDir = Path.GetDirectoryName(filePath);
            while (currentDir != null)
            {
                if (Directory.EnumerateFiles(currentDir, "*.csproj").Any())
                {
                    return currentDir;
                }

                currentDir = Directory.GetParent(currentDir)?.FullName;
            }
        }
        catch
        {
        }

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
                if (csproj != null)
                {
                    return csproj;
                }

                currentDir = Directory.GetParent(currentDir)?.FullName;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? GetSourceNamespace(ClassDeclarationSyntax targetClass)
    {
        for (SyntaxNode? current = targetClass.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case FileScopedNamespaceDeclarationSyntax fileScoped:
                    return fileScoped.Name.ToString();
                case NamespaceDeclarationSyntax blockScoped:
                    return blockScoped.Name.ToString();
            }
        }

        return null;
    }

    private static string? TryReadTestProjectRootNamespace(string testProjectDirectory)
    {
        if (string.IsNullOrWhiteSpace(testProjectDirectory) || !Directory.Exists(testProjectDirectory))
        {
            return null;
        }

        try
        {
            var csproj = Directory.GetFiles(testProjectDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (csproj is null)
            {
                return null;
            }

            var doc = XDocument.Load(csproj);
            var value = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?
                .Value?.Trim();

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveTestNamespace(string testProjectDirectory, ClassDeclarationSyntax targetClass)
    {
        var fromProject = TryReadTestProjectRootNamespace(testProjectDirectory);
        if (!string.IsNullOrWhiteSpace(fromProject))
        {
            return fromProject!;
        }

        var fromSource = GetSourceNamespace(targetClass);
        if (!string.IsNullOrWhiteSpace(fromSource))
        {
            return fromSource!.EndsWith(".Tests", StringComparison.Ordinal)
                ? fromSource!
                : fromSource + ".Tests";
        }

        return "NetAI.Generated.Tests";
    }

    private static string DeriveRootNamespaceFromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return SanitizeNamespace(name);
    }

    private static string DeriveRootNamespaceFromProjectFile(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return string.Empty;
        }

        return SanitizeNamespace(Path.GetFileNameWithoutExtension(projectPath));
    }

    private static string SanitizeNamespace(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var sanitized = Regex.Replace(candidate, @"[^\w\.]", "_");
        if (sanitized.Length == 0)
        {
            return string.Empty;
        }

        if (char.IsDigit(sanitized[0]))
        {
            sanitized = "_" + sanitized;
        }

        return sanitized;
    }

    // ---------------------------------------------------------------- framework helpers

    private static TestFramework ParseTestFramework(AiTestingConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (config.Frameworks is not null && Enum.TryParse(config.Frameworks.TestFramework, true, out TestFramework framework) &&
            framework != TestFramework.Unknown && Enum.IsDefined(typeof(TestFramework), framework))
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
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (config.Frameworks is not null && Enum.TryParse(config.Frameworks.MockingFramework, true, out MockFramework framework) &&
            framework != MockFramework.Unknown && Enum.IsDefined(typeof(MockFramework), framework))
        {
            return framework;
        }

        throw new ArgumentException(
            $"Unsupported mocking framework '{config.Frameworks?.MockingFramework}'. Supported values are moq, nsubstitute, and fakeiteasy.",
            nameof(config));
    }

    private static string GetTestTemplate(TestFramework testFramework)
    {
        return testFramework switch
        {
            TestFramework.NUnit => "nunit",
            TestFramework.MSTest => "mstest",
            TestFramework.xUnit => "xunit",
            _ => throw new ArgumentOutOfRangeException(nameof(testFramework), testFramework, "Unsupported test framework.")
        };
    }

    private static string GetTestFrameworkName(TestFramework testFramework)
    {
        return testFramework switch
        {
            TestFramework.NUnit => "NUnit",
            TestFramework.MSTest => "MSTest",
            TestFramework.xUnit => "xUnit",
            _ => throw new ArgumentOutOfRangeException(nameof(testFramework), testFramework, "Unsupported test framework.")
        };
    }

    private static string GetTestAttribute(TestFramework testFramework)
    {
        return testFramework switch
        {
            TestFramework.NUnit => "[Test]",
            TestFramework.MSTest => "[TestMethod]",
            TestFramework.xUnit => "[Fact]",
            _ => throw new ArgumentOutOfRangeException(nameof(testFramework), testFramework, "Unsupported test framework.")
        };
    }

    private static string GetTestFrameworkNamespace(TestFramework testFramework)
    {
        return testFramework switch
        {
            TestFramework.NUnit => "NUnit.Framework",
            TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting",
            TestFramework.xUnit => "Xunit",
            _ => throw new ArgumentOutOfRangeException(nameof(testFramework), testFramework, "Unsupported test framework.")
        };
    }

    private static string StripAiAreaMarkers(string code)
        => string.IsNullOrEmpty(code) ? code : AiAreaMarkerLineRegex.Replace(code, string.Empty);

    private static int EstimateCyclomaticComplexity(MethodDeclarationSyntax method)
    {
        var complexity = 1;

        foreach (var node in method.DescendantNodes())
        {
            complexity += node switch
            {
                IfStatementSyntax => 1,
                SwitchSectionSyntax => 1,
                ForStatementSyntax => 1,
                ForEachStatementSyntax => 1,
                WhileStatementSyntax => 1,
                DoStatementSyntax => 1,
                CatchClauseSyntax => 1,
                ConditionalExpressionSyntax => 1,
                BinaryExpressionSyntax b
                    when b.IsKind(SyntaxKind.LogicalAndExpression)
                         || b.IsKind(SyntaxKind.LogicalOrExpression)
                         || b.IsKind(SyntaxKind.CoalesceExpression) => 1,
                _ => 0
            };
        }

        return complexity;
    }

    private static bool IsWithinSizeLimit(MethodDeclarationSyntax method, int maxChars, out int actualChars)
    {
        actualChars = method.ToFullString().Length;
        return actualChars <= maxChars;
    }
}