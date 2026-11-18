using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

namespace NetAI.TestGenerator.Core;

/// <summary>Coordinates source analysis, AI-generated unit tests, and optional compile validation.</summary>
public sealed class TetsGeneratorOrchestrator : IDisposable
{
    private const int DefaultMaxMethodChars = 25_000;

    private const int MaxAiRetries = 3;

    private const int MaxEnvRetries = 2;

    private static readonly TimeSpan EnvRetryDelay = TimeSpan.FromSeconds(5);

    private const string GenerationSystemPrompt =
        "You are a C# testing expert. Respond only with runnable C# code and no explanations. " +
        "If a <BindingSkeleton> block is present, preserve its structure verbatim " +
        "(usings, namespace, class name, fields, constructor, test attribute) and only fill in the test body.";

    private const string RepairSystemPrompt =
        "You are a precise C# compiler assistant. Your only task is to accurately fix " +
        "syntax and compilation errors in the provided C# code " +
        "and return runnable code without textual explanations.\n";

    private static readonly Regex AiAreaMarkerLineRegex = new(@"^[ \t]*//[^\r\n]*AI AREA[^\r\n]*\r?\n" + @"|^[ \t]*//[ \t]*=+[ \t]*\r?\n",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TestCodeBlockRegex = new(
        @"```(?:csharp|cs)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AiTestingConfig? _config;

    private readonly AdhocWorkspace _formatWorkspace = new();

    private readonly HttpClient? _httpClient;

    private readonly MockFramework _mockFramework;

    private readonly AiPromptBuilderSimple _promptBuilder = new();

    private readonly TestCodeBeautifier _testCodeBeautifier = new();

    private readonly TestFramework _testFramework;

    private bool _disposed;

    private LocalLlmClient? _llmClient;

    /// <summary>Creates an orchestrator for generating tests from source files.</summary>
    public TetsGeneratorOrchestrator(HttpClient? httpClient = null) : this(TestFramework.xUnit, MockFramework.Unknown, null, httpClient)
    {
    }

    /// <summary>Creates an orchestrator using the configured test and mocking frameworks.</summary>
    /// <exception cref="ArgumentException">The configured test or mocking framework is not supported.</exception>
    /// <exception cref="InvalidOperationException">The AI connection settings are invalid.</exception>
    public TetsGeneratorOrchestrator(AiTestingConfig config, HttpClient? httpClient = null) : this(ParseTestFramework(config),
        ParseMockFramework(config), config, httpClient)
    {
    }

    private TetsGeneratorOrchestrator(TestFramework testFramework, MockFramework mockFramework, AiTestingConfig? config,
        HttpClient? httpClient)
    {
        _testFramework = testFramework;
        _mockFramework = mockFramework;
        _config = config;
        _httpClient = httpClient;

        config?.AiConfiguration?.Validate();
    }

    private LocalLlmClient LlmClient
    {
        get
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TetsGeneratorOrchestrator));
            }

            return _llmClient ??= new LocalLlmClient(_config?.AiConfiguration ?? new LlmConnectionSettings(), _httpClient);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _llmClient?.Dispose();
        _formatWorkspace.Dispose();
    }

    /// <summary>
    /// Generates tests for uncovered methods of every top-level (non-nested, non-test) class or record
    /// found in the supplied source file.
    /// </summary>
    /// <param name="sourceFilePath">The C# source file to generate tests for.</param>
    /// <param name="testProjectDirectory">The directory of the test project (created if missing).</param>
    /// <param name="log">Optional log sink.</param>
    /// <param name="compilation">Optional compilation that contains <paramref name="sourceFilePath"/> for semantic analysis.</param>
    /// <param name="promptOnly">When true, prompts are saved and AI drafts are written without compile validation.</param>
    /// <param name="solutionPath">Optional solution to load via MSBuild (net10.0 only).</param>
    /// <param name="projectPath">Optional project to load via MSBuild (net10.0 only).</param>
    /// <param name="cancellationToken">Cancels the operation; the test file is restored to its original state.</param>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public async Task<FileGenerationResult> ProcessProjectAsync(string sourceFilePath, string testProjectDirectory,
        GeneratorLogger? log = null, Compilation? compilation = null, bool promptOnly = false, string? solutionPath = null,
        string? projectPath = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return FileGenerationResult.Fail("Source file empty or missing.");
        }

        if (string.IsNullOrWhiteSpace(testProjectDirectory))
        {
            return FileGenerationResult.Fail("Test project directory must not be empty.");
        }

        sourceFilePath = Path.GetFullPath(sourceFilePath);
        testProjectDirectory = Path.GetFullPath(testProjectDirectory);

        IDisposable? msbuildHandle = null;

        try
        {
            string sourceCode;
            using (var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                sourceCode = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var sourceTree = FindSyntaxTree(compilation, sourceFilePath) ??
                             CSharpSyntaxTree.ParseText(sourceCode, path: sourceFilePath, cancellationToken: cancellationToken);

            if (SelectTargetClasses(sourceTree).Count == 0)
            {
                return FileGenerationResult.Nothing("No class found in source file.");
            }

            var testProject = TestProjectInfo.Load(testProjectDirectory);

            var profile = new TestFrameworkProfile(_testFramework, _mockFramework, _config?.Frameworks?.UseFluentAssertions ?? true,
                _config?.Frameworks?.UseAutoFixture ?? false, testProject.PackageIds);

            RoslynDllTestabilityAnalyzer? semanticAnalyzer = null;
            var effectiveCompilation = compilation;

#if !NETSTANDARD2_0
            if (!string.IsNullOrWhiteSpace(solutionPath) || !string.IsNullOrWhiteSpace(projectPath))
            {
                semanticAnalyzer = new RoslynDllTestabilityAnalyzer(new AnalyzerOptions { TestFrameworkProfile = profile });

                var target = !string.IsNullOrWhiteSpace(solutionPath)
                    ? $"solution '{Path.GetFileName(solutionPath)}'"
                    : $"project '{Path.GetFileName(projectPath)}'";

                try
                {
                    log.Info($"[NetAI] Loading {target} via MSBuild...");
                    var loaded = await semanticAnalyzer.LoadCompilationFromMsbuildAsync(solutionPath, projectPath,
                        Path.GetFileName(sourceFilePath), log.AsInfoAction(), cancellationToken).ConfigureAwait(false);

                    msbuildHandle = loaded.Workspace;
                    effectiveCompilation = loaded.Compilation ?? compilation;

                    log.Info(loaded.Compilation is null
                        ? "[NetAI] MSBuild returned no compilation; falling back to the supplied compilation."
                        : $"[NetAI] MSBuild context loaded ({target}); {loaded.Compilation.SyntaxTrees.Count()} syntax trees.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Warning($"[NetAI] MSBuild load failed: {ex.Message}; falling back to in-memory compilation.");
                    msbuildHandle?.Dispose();
                    msbuildHandle = null;
                    effectiveCompilation = compilation;
                }
            }
#endif

            if (semanticAnalyzer is null && effectiveCompilation is not null)
            {
                semanticAnalyzer = new RoslynDllTestabilityAnalyzer(new AnalyzerOptions { TestFrameworkProfile = profile });
                log.Info("[NetAI] Semantic analysis is available via in-memory compilation; AI prompts will include semantic context.");
            }
            else if (effectiveCompilation is null)
            {
                log.Info("[NetAI] No compilation or MSBuild path was provided; using syntax-only analysis.");
            }

            // Always work on the syntax tree that belongs to the effective compilation; otherwise
            // GetSemanticModel throws "SyntaxTree is not part of the compilation".
            sourceTree = FindSyntaxTree(effectiveCompilation, sourceFilePath) ?? sourceTree;
            var targetClasses = SelectTargetClasses(sourceTree);

            log.Info($"[NetAI] File '{Path.GetFileName(sourceFilePath)}' contains {targetClasses.Count} class(es) to process.");

            var testProjectManager = new TestProjectManager(testFramework: _testFramework, mockFramework: _mockFramework);
            foreach (var packageId in _config?.AllowedPackages ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(packageId))
                {
                    testProjectManager.AllowedPackageIds.Add(packageId.Trim());
                }
            }

            var testTemplate = GetTestTemplate(_testFramework);

            var compilerService = new TestProjectManagerCompilerService((code, ct) => testProjectManager.SetupAndValidateTestAsync(
                sourceFilePath, code, testTemplate: testTemplate, cancellationToken: ct, testProjectDirectoryOverride: testProjectDirectory));

            var context = new RunContext(
                sourceFilePath,
                testProjectDirectory,
                log,
                promptOnly,
                promptOnly ? FindProjectDirectory(sourceFilePath) : null,
                effectiveCompilation,
                semanticAnalyzer,
                profile,
                BuildProjectContextHint(sourceFilePath, testProjectDirectory, testProject),
                testProject,
                testProjectManager,
                compilerService,
                new TestCodeProcessor(compilerService));

            foreach (var targetClass in targetClasses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessClassAsync(context, targetClass, cancellationToken).ConfigureAwait(false);
            }

            return context.ToResult();
        }
        catch (OperationCanceledException)
        {
            log.Warning($"[NetAI] Cancelled while processing '{Path.GetFileName(sourceFilePath)}'.");
            throw;
        }
        catch (Exception ex)
        {
            log.Error($"[NetAI] Processing '{Path.GetFileName(sourceFilePath)}' failed: {ex}");
            return FileGenerationResult.Fail(ex.Message);
        }
        finally
        {
            try
            {
                msbuildHandle?.Dispose();
            }
            catch
            {
                // Disposing the MSBuild workspace must never hide the actual result.
            }
        }
    }

    private async Task ProcessClassAsync(RunContext ctx, TypeDeclarationSyntax targetClass, CancellationToken ct)
    {
        var log = ctx.Log;
        var className = targetClass.Identifier.Text;
        var testNamespace = ResolveTestNamespace(ctx.TestProject, targetClass);
        var testClassName = $"{className}Tests";
        var testFilePath = Path.Combine(ctx.TestProjectDirectory, $"{testClassName}.cs");

        BuildLogger.Info(testFilePath);
        log.Info($"[NetAI] Starting analysis for class: {className}");

        // Keep the original test file in memory. Validation overwrites the file on disk, so it is
        // restored in the finally block no matter how the method loop ends (success, failure, exception, cancel).
        string? originalContent = null;
        if (File.Exists(testFilePath))
        {
            try
            {
                originalContent = File.ReadAllText(testFilePath);
            }
            catch (Exception ex)
            {
                log.Error($"[NetAI] Cannot read existing test file {testFilePath}: {ex.Message}. Skipping class to avoid data loss.");
                ctx.Failed++;
                return;
            }
        }

        var coveredTestNames = GetTestMethodNames(originalContent);
        var snippets = new List<Snippet>();
        var failedInClass = 0;

        try
        {
            foreach (var method in SelectSourceMethods(targetClass))
            {
                ct.ThrowIfCancellationRequested();

                var methodName = method.Identifier.Text;
                if (IsCoveredByExistingTest(coveredTestNames, methodName))
                {
                    log.Info($"[NetAI] Method '{methodName}' is already covered by an existing test. Skipping.");
                    ctx.AlreadyCovered++;
                    continue;
                }

                try
                {
                    var outcome = await GenerateForMethodAsync(ctx, targetClass, method, testClassName, testNamespace, ct)
                        .ConfigureAwait(false);

                    if (outcome.Snippet is { } snippet)
                    {
                        snippets.Add(snippet);
                    }
                    else if (outcome.IsFailure)
                    {
                        failedInClass++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Error($"[NetAI] Generating a test for '{className}.{methodName}' failed: {ex.Message}");
                    failedInClass++;
                }
            }
        }
        finally
        {
            RestoreTestFile(testFilePath, originalContent, log);
        }

        ctx.Failed += failedInClass;

        if (snippets.Count == 0)
        {
            return;
        }

        var written = await WriteClassTestFileAsync(ctx, testFilePath, testClassName, testNamespace, originalContent, snippets, ct)
            .ConfigureAwait(false);

        foreach (var snippet in snippets)
        {
            if (!written.Contains(snippet))
            {
                ctx.Failed++;
            }
            else if (snippet.Kind == SnippetKind.SkipStub)
            {
                ctx.SkipStubs++;
            }
            else
            {
                ctx.Generated++;
            }
        }

        if (written.Count > 0)
        {
            ctx.WrittenFiles.Add(testFilePath);
        }
    }

    /// <summary>
    /// Merges the original file with the collected snippets, validates the merged result once and writes it.
    /// Returns the snippets that actually ended up in the file.
    /// </summary>
    private async Task<IReadOnlyCollection<Snippet>> WriteClassTestFileAsync(RunContext ctx, string testFilePath, string testClassName,
        string testNamespace, string? originalContent, IReadOnlyList<Snippet> snippets, CancellationToken ct)
    {
        var log = ctx.Log;
        Directory.CreateDirectory(ctx.TestProjectDirectory);

        string Merge(IEnumerable<Snippet> parts)
        {
            var sources = new List<string>();
            if (originalContent is not null)
            {
                sources.Add(originalContent);
            }

            sources.AddRange(parts.Select(p => p.Code));
            return MergeCollectedTestClasses(testClassName, testNamespace, sources);
        }

        if (ctx.PromptOnly)
        {
            File.WriteAllText(testFilePath, Merge(snippets), Encoding.UTF8);
            log.Info($"[NetAI] Wrote {snippets.Count} unvalidated draft(s) to {testFilePath} (prompt-only mode).");
            return snippets;
        }

        // Template tests and skip stubs were never compiled on their own, and merging can change constructors/fields.
        // So the final file is validated once; if it does not compile, retry with the individually validated AI tests only.
        var attempts = new List<IReadOnlyList<Snippet>> { snippets };
        var validatedOnly = snippets.Where(s => s.Kind == SnippetKind.AiValidated).ToList();
        if (validatedOnly.Count > 0 && validatedOnly.Count < snippets.Count)
        {
            attempts.Add(validatedOnly);
        }

        TestGenerationResult? lastResult = null;
        foreach (var attempt in attempts)
        {
            ct.ThrowIfCancellationRequested();

            var merged = Merge(attempt);
            lastResult = await ctx.TestProjectManager.SetupAndValidateTestAsync(ctx.SourceFilePath, merged,
                    testTemplate: GetTestTemplate(_testFramework), cancellationToken: ct,
                    testProjectDirectoryOverride: ctx.TestProjectDirectory)
                .ConfigureAwait(false);

            if (lastResult.IsSuccess)
            {
                log.Info($"[NetAI] Wrote {attempt.Count} new test method(s) to {testFilePath}.");
                return attempt.ToList();
            }

            if (lastResult.IsEnvironmentIssue)
            {
                // The build is blocked (locked files), not the code: keep the merged result but say it is unverified.
                File.WriteAllText(testFilePath, merged, Encoding.UTF8);
                log.Warning($"[NetAI] Wrote {attempt.Count} new test method(s) to {testFilePath}, but the final build could not run " +
                            "(files locked by another process). Rebuild the test project to verify.");
                return attempt.ToList();
            }

            log.Warning($"[NetAI] Merged test file for '{testClassName}' with {attempt.Count} new member group(s) does not compile.");
        }

        RestoreTestFile(testFilePath, originalContent, log);
        log.Error($"[NetAI] Could not write a compilable {testClassName}.cs; the original file was kept." +
                  (lastResult?.CompilerErrors is { Length: > 0 } errors ? "\n" + string.Join("\n", errors) : string.Empty));
        return Array.Empty<Snippet>();
    }

    private async Task<MethodOutcome> GenerateForMethodAsync(RunContext ctx, TypeDeclarationSyntax targetClass,
        MethodDeclarationSyntax method, string testClassName, string testNamespace, CancellationToken ct)
    {
        var log = ctx.Log;
        var className = targetClass.Identifier.Text;
        var methodName = method.Identifier.Text;
        var (model, methodSymbol) = ResolveMethodSymbol(ctx.Compilation, method);

        var maxChars = _config?.MaxMethodChars is > 0 and var configured ? configured : DefaultMaxMethodChars;
        if (!IsWithinSizeLimit(method, maxChars, out var methodChars))
        {
            log.Info($"[NetAI] Method '{methodName}' is {methodChars:N0} chars (limit: {maxChars:N0}). " +
                     "Generating skip stub instead of a real test.");

            if (methodSymbol is null)
            {
                log.Warning($"[NetAI] Could not resolve '{methodName}' to build a skip stub; skipping entirely.");
                return MethodOutcome.Ignored;
            }

            var skipReason = $"method is {methodChars:N0} chars (limit: {maxChars:N0}). Refactor into smaller, focused methods.";
            var skipResult = UnitTestSkeletonGenerator.GenerateFromMethod(methodSymbol, ctx.Profile, false,
                UnitTestSkeletonGenerator.TestStrategy.Skip, null, skipReason);

            return MethodOutcome.From(new Snippet(methodName, StripAiAreaMarkers(skipResult.TestSkeleton), SnippetKind.SkipStub));
        }

        var complexity = EstimateCyclomaticComplexity(method);
        if (complexity > 20)
        {
            log.Info($"[NetAI] Method '{methodName}' has cyclomatic complexity {complexity}. " +
                     "The generated test will likely cover only the happy path — review manually.");
        }

        log.Info($"[NetAI] Missing test detected for method: {methodName}. Triggering AI generation...");

        if (model is not null && methodSymbol is not null)
        {
            var trivial = TrivialTestGenerator.TryGenerate(method, methodSymbol, model, ctx.Profile, false);

            if (trivial.ShouldSkipEntirely)
            {
                log.Info($"[NetAI] Method '{methodName}' is marked as not-implemented; skipping entirely.");
                return MethodOutcome.Ignored;
            }

            if (trivial.TestSnippet is not null)
            {
                log.Info($"[NetAI] Method '{methodName}' is trivial; generated template test without LLM.");
                return MethodOutcome.From(new Snippet(methodName, trivial.TestSnippet, SnippetKind.Template));
            }
        }

        var classSkeleton = BuildClassSkeleton(targetClass, method);
        BuildLogger.Info(classSkeleton);

        var semanticHint = await BuildSemanticHintAsync(ctx.SemanticAnalyzer, ctx.Compilation, method, log, ct).ConfigureAwait(false);
        BuildLogger.Info(semanticHint.Xml);

        if (ShouldGenerateSkipStub(semanticHint, out var skipStubReason))
        {
            var skipTestClass = GenerateRoslynSkipTestClass(testClassName, testNamespace, methodName, semanticHint, skipStubReason);
            log.Info($"[NetAI] Method '{methodName}' skipped via Roslyn (no AI call): {skipStubReason}");
            return MethodOutcome.From(new Snippet(methodName, StripAiAreaMarkers(skipTestClass), SnippetKind.SkipStub));
        }

        var basePrompt = BuildBasePrompt(className, methodName, semanticHint.Xml, ctx.ProjectContext, classSkeleton,
            semanticHint.Skeleton, semanticHint.RequiresSta);
        BuildLogger.Info(basePrompt);

        if (ctx.PromptOnly)
        {
            var promptDirectory = Path.Combine(ctx.HostProjectDirectory ?? Path.GetDirectoryName(ctx.SourceFilePath)!, "obj", "netai",
                "prompts");
            Directory.CreateDirectory(promptDirectory);
            var promptPath = Path.Combine(promptDirectory, $"{className}.{methodName}.prompt.md");
            File.WriteAllText(promptPath, basePrompt, Encoding.UTF8);
            log.Info($"[NetAI] Prompt with semantic context saved: {promptPath}");
        }

        var response = await LlmClient.AskAsync(basePrompt, GenerationSystemPrompt, ct).ConfigureAwait(false);
        var testMethodCode = ExtractTestClass(response);
        BuildLogger.Info(testMethodCode);

        if (!ContainsTestMethod(testMethodCode))
        {
            log.Warning($"[NetAI] The AI response for '{methodName}' contains no test method.");
            return MethodOutcome.Failed;
        }

        if (ctx.PromptOnly)
        {
            log.Info($"[NetAI] Test draft for '{methodName}' collected; compile validation skipped.");
            return MethodOutcome.From(new Snippet(methodName, StripAiAreaMarkers(testMethodCode), SnippetKind.Draft));
        }

        var validatedCode = await ValidateAndRepairAsync(ctx, methodName, testClassName, testNamespace, testMethodCode, semanticHint, ct)
            .ConfigureAwait(false);

        return validatedCode is null
            ? MethodOutcome.Failed
            : MethodOutcome.From(new Snippet(methodName, StripAiAreaMarkers(validatedCode), SnippetKind.AiValidated));
    }

    /// <summary>Compiles the AI test and runs Roslyn/AI repair rounds. Returns the compiling code or <c>null</c>.</summary>
    private async Task<string?> ValidateAndRepairAsync(RunContext ctx, string methodName, string testClassName, string testNamespace,
        string testMethodCode, SemanticHint semanticHint, CancellationToken ct)
    {
        var log = ctx.Log;
        TestGenerationResult? result = null;

        var aiAttempts = 0;
        var envAttempts = 0;
        var roslynFixApplied = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!ContainsTestMethod(testMethodCode))
            {
                log.Warning($"[NetAI] The repaired code for '{methodName}' no longer contains a test method.");
                break;
            }

            var validationClassStructure = PrepareValidationStructure(testClassName, testNamespace, testMethodCode, semanticHint.Skeleton);

            // The processor compiles (and fixes missing usings) through the compiler service; reuse its last
            // build result instead of running another identical "dotnet build".
            ctx.CompilerService.Reset();
            validationClassStructure = await ctx.TestCodeProcessor
                .ProcessTestClassAsync(validationClassStructure, _testFramework, _mockFramework, ct).ConfigureAwait(false);

            result = ctx.CompilerService.LastResult ?? await ctx.TestProjectManager.SetupAndValidateTestAsync(ctx.SourceFilePath,
                    validationClassStructure, testTemplate: GetTestTemplate(_testFramework), cancellationToken: ct,
                    testProjectDirectoryOverride: ctx.TestProjectDirectory)
                .ConfigureAwait(false);

            BuildLogger.Warning(result.CompilerErrors);

            if (result.IsSuccess)
            {
                return ExtractTestClass(result.TestClassCode ?? validationClassStructure);
            }

            if (result.IsEnvironmentIssue)
            {
                if (++envAttempts > MaxEnvRetries)
                {
                    log.Warning($"[NetAI] Gave up on '{methodName}' after {MaxEnvRetries} environment retries. " +
                                "Close Visual Studio / the running app and try again.");
                    break;
                }

                log.Info($"[NetAI] Environment issue while validating '{methodName}' (env retry {envAttempts}/{MaxEnvRetries}). " +
                         "A referenced assembly is locked by another process; retrying without asking the AI.");
                await Task.Delay(EnvRetryDelay, ct).ConfigureAwait(false);
                continue;
            }

            if (!roslynFixApplied && result.CompilerErrors?.Any() == true)
            {
                var codeForRoslyn = result.TestClassCode ?? validationClassStructure;
                var roslynFixed = await _testCodeBeautifier.TryFixCompilerErrorsAsync(
                    codeForRoslyn, result.CompilerErrors, _testFramework, _mockFramework, ct).ConfigureAwait(false);

                roslynFixApplied = true;

                if (!string.Equals(roslynFixed, codeForRoslyn, StringComparison.Ordinal))
                {
                    BuildLogger.Info(roslynFixed);
                    log.Info("[NetAI] Roslyn automatically added missing using directives; retrying compilation without AI.");
                    testMethodCode = ExtractTestClass(roslynFixed);
                    continue;
                }
            }

            if (aiAttempts >= MaxAiRetries)
            {
                log.Warning($"[NetAI] Reached {MaxAiRetries} AI repair attempts for '{methodName}'. Giving up.");
                break;
            }

            aiAttempts++;
            log.Info($"[NetAI] Test for '{methodName}' failed compilation (AI attempt {aiAttempts}/{MaxAiRetries}). Running AI repair loop...");

            if (result.CompilerErrors?.Any(i => i.IndexOf("Could not automatically resolve a NuGet package for the namespace(s)",
                    StringComparison.Ordinal) >= 0) == true)
            {
                log.Warning("[NetAI] Missing NuGet dependencies detected in generated test.");
            }

            if (result.RequiresRegeneration)
            {
                log.Info("[NetAI] The manager flagged the test code for regeneration (likely hallucinated types or invented members). " +
                         "The repair prompt will include concrete hints.");
            }

            var errorsText = string.Join("\n", SelectRelevantErrors(result));
            BuildLogger.Info(errorsText);

            var errorPrompt = _promptBuilder.FixUnittestPromptSimple(errorsText, result.TestClassCode ?? validationClassStructure,
                _testFramework, _mockFramework);
            BuildLogger.Info(errorPrompt);

            var correctedOutput = await LlmClient.AskAsync(errorPrompt, RepairSystemPrompt, ct).ConfigureAwait(false);
            testMethodCode = ExtractTestClass(correctedOutput);
            BuildLogger.Info(testMethodCode);

            // A new AI version gets its own chance for the cheap Roslyn using-fix.
            roslynFixApplied = false;
        }

        log.Warning($"[NetAI] Could not generate a compilable test for '{methodName}'.");
        if (result?.CompilerErrors is { Length: > 0 } finalErrors)
        {
            log.Debug($"[NetAI] Final compiler errors:\n{string.Join("\n", finalErrors)}");
        }

        return null;
    }

    /// <summary>Keeps only the errors of the generated test file so the AI is not distracted by unrelated files.</summary>
    private static IReadOnlyList<string> SelectRelevantErrors(TestGenerationResult result)
    {
        var allErrors = result.CompilerErrors ?? Array.Empty<string>();
        var currentTestFileName = Path.GetFileName(result.TestClassPath ?? string.Empty);

        if (string.IsNullOrEmpty(currentTestFileName))
        {
            return allErrors;
        }

        var relevant = allErrors
            .Where(e => !e.Contains(", line ", StringComparison.Ordinal) || e.Contains(currentTestFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return relevant.Length == 0 ? allErrors : relevant;
    }

    private static void RestoreTestFile(string testFilePath, string? originalContent, GeneratorLogger? log)
    {
        try
        {
            if (originalContent is null)
            {
                if (File.Exists(testFilePath))
                {
                    File.Delete(testFilePath);
                }

                return;
            }

            if (!File.Exists(testFilePath) || !string.Equals(File.ReadAllText(testFilePath), originalContent, StringComparison.Ordinal))
            {
                File.WriteAllText(testFilePath, originalContent, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            log.Error($"[NetAI] Could not restore test file {testFilePath}: {ex.Message}");
        }
    }

    private static SyntaxTree? FindSyntaxTree(Compilation? compilation, string sourceFilePath)
    {
        if (compilation is null)
        {
            return null;
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrEmpty(tree.FilePath))
            {
                continue;
            }

            try
            {
                if (string.Equals(Path.GetFullPath(tree.FilePath), sourceFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    return tree;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Generated or in-memory trees may carry paths that are not valid file paths.
            }
        }

        return null;
    }

    private static (SemanticModel? Model, IMethodSymbol? Symbol) ResolveMethodSymbol(Compilation? compilation,
        MethodDeclarationSyntax method)
    {
        if (compilation is null || !compilation.ContainsSyntaxTree(method.SyntaxTree))
        {
            return (null, null);
        }

        var model = compilation.GetSemanticModel(method.SyntaxTree);
        return (model, model.GetDeclaredSymbol(method));
    }

    private static List<TypeDeclarationSyntax> SelectTargetClasses(SyntaxTree tree)
    {
        return tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax)
            .Where(t => t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .Where(t => !t.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)))
            .Where(t => !t.Identifier.Text.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
            .Where(t => !t.Members.OfType<MethodDeclarationSyntax>().Any(HasTestAttribute))
            .ToList();
    }

    /// <summary>Methods declared directly on the class (not on nested types) that have a body.</summary>
    private static IEnumerable<MethodDeclarationSyntax> SelectSourceMethods(TypeDeclarationSyntax targetClass)
    {
        return targetClass.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Body is not null || m.ExpressionBody is not null);
    }

    private static HashSet<string> GetTestMethodNames(string? testFileContent)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(testFileContent))
        {
            return names;
        }

        foreach (var method in CSharpSyntaxTree.ParseText(testFileContent!).GetCompilationUnitRoot().DescendantNodes()
                     .OfType<MethodDeclarationSyntax>().Where(HasTestAttribute))
        {
            names.Add(method.Identifier.Text);
        }

        return names;
    }

    private static bool IsCoveredByExistingTest(IEnumerable<string> testNames, string methodName)
    {
        return testNames.Any(t => t.Equals(methodName, StringComparison.OrdinalIgnoreCase) ||
                                  t.StartsWith(methodName + "_", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsTestMethod(string code)
    {
        return !string.IsNullOrWhiteSpace(code) &&
               CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                   .Any(HasTestAttribute);
    }

    private enum SnippetKind
    {
        /// <summary>AI test that compiled on its own.</summary>
        AiValidated,

        /// <summary>Template test without AI (never compiled on its own).</summary>
        Template,

        /// <summary>Skip/ignore stub.</summary>
        SkipStub,

        /// <summary>Unvalidated AI draft (prompt-only mode).</summary>
        Draft
    }

    private sealed record Snippet(string MethodName, string Code, SnippetKind Kind);

    private sealed record MethodOutcome(Snippet? Snippet, bool IsFailure)
    {
        public static readonly MethodOutcome Failed = new(null, true);

        public static readonly MethodOutcome Ignored = new(null, false);

        public static MethodOutcome From(Snippet snippet) => new(snippet, false);
    }

    private sealed class RunContext
    {
        public RunContext(string sourceFilePath, string testProjectDirectory, GeneratorLogger? log, bool promptOnly,
            string? hostProjectDirectory, Compilation? compilation, RoslynDllTestabilityAnalyzer? semanticAnalyzer,
            TestFrameworkProfile profile, string projectContext, TestProjectInfo testProject, TestProjectManager testProjectManager,
            TestProjectManagerCompilerService compilerService, TestCodeProcessor testCodeProcessor)
        {
            SourceFilePath = sourceFilePath;
            TestProjectDirectory = testProjectDirectory;
            Log = log;
            PromptOnly = promptOnly;
            HostProjectDirectory = hostProjectDirectory;
            Compilation = compilation;
            SemanticAnalyzer = semanticAnalyzer;
            Profile = profile;
            ProjectContext = projectContext;
            TestProject = testProject;
            TestProjectManager = testProjectManager;
            CompilerService = compilerService;
            TestCodeProcessor = testCodeProcessor;
        }

        public string SourceFilePath { get; }

        public string TestProjectDirectory { get; }

        public GeneratorLogger? Log { get; }

        public bool PromptOnly { get; }

        public string? HostProjectDirectory { get; }

        public Compilation? Compilation { get; }

        public RoslynDllTestabilityAnalyzer? SemanticAnalyzer { get; }

        public TestFrameworkProfile Profile { get; }

        public string ProjectContext { get; }

        public TestProjectInfo TestProject { get; }

        public TestProjectManager TestProjectManager { get; }

        public TestProjectManagerCompilerService CompilerService { get; }

        public TestCodeProcessor TestCodeProcessor { get; }

        public int Generated { get; set; }

        public int SkipStubs { get; set; }

        public int AlreadyCovered { get; set; }

        public int Failed { get; set; }

        public List<string> WrittenFiles { get; } = new();

        public FileGenerationResult ToResult()
        {
            var produced = Generated + SkipStubs;
            var status = Failed == 0
                ? FileGenerationStatus.Success
                : produced > 0
                    ? FileGenerationStatus.PartialSuccess
                    : FileGenerationStatus.Failed;

            return new FileGenerationResult
            {
                Status = status,
                Message = $"generated: {Generated}, skip stubs: {SkipStubs}, already covered: {AlreadyCovered}, failed: {Failed}",
                Generated = Generated,
                SkipStubs = SkipStubs,
                AlreadyCovered = AlreadyCovered,
                Failed = Failed,
                WrittenFiles = WrittenFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
    }

    private static bool ShouldGenerateSkipStub(SemanticHint hint, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrEmpty(hint.Xml))
        {
            return false;
        }

        switch (hint.Strategy)
        {
            case UnitTestSkeletonGenerator.TestStrategy.Skip:
                reason = "Semantic analysis: Skip strategy.";
                return true;

            case UnitTestSkeletonGenerator.TestStrategy.RefactorFirst:
                reason = "Semantic analysis: RefactorFirst. " +
                         (hint.Blockers.Count > 0 ? string.Join(" | ", hint.Blockers) : string.Empty);
                return true;

            case UnitTestSkeletonGenerator.TestStrategy.Reflection:
                if (hint.Blockers.Any(b => b.IndexOf("Private", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    reason = "Method is private and not directly testable. " +
                             (hint.Blockers.Count > 0 ? string.Join(" | ", hint.Blockers) : string.Empty);
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static IReadOnlyList<string> BuildSkipHintLines(SemanticHint hint)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(hint.Verdict))
        {
            lines.Add("Verdict: " + hint.Verdict);
        }

        if (hint.Blockers.Count > 0)
        {
            lines.Add("Blockers:");
            foreach (var b in hint.Blockers)
            {
                lines.Add("  - " + b);
            }
        }

        if (hint.SourceRefactoringRecommendations.Count > 0)
        {
            lines.Add("Recommended source refactorings:");
            foreach (var r in hint.SourceRefactoringRecommendations)
            {
                lines.Add("  - " + r);
            }
        }

        if (hint.AsyncVoidRefactoringLines.Count > 0)
        {
            lines.Add("Suggested refactoring (async void → awaitable Task):");
            foreach (var line in hint.AsyncVoidRefactoringLines)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private string GenerateRoslynSkipTestClass(
        string testClassName,
        string testNamespace,
        string methodName,
        SemanticHint hint,
        string skipReason)
    {
        var testMethodName = $"{methodName}_IsNotDirectlyTestable";
        var hintLines = BuildSkipHintLines(hint);

        var attributeReason = skipReason.Length > 100
            ? skipReason.Substring(0, 97) + "..."
            : skipReason;

        var sb = new StringBuilder();
        sb.AppendLine($"using {GetTestFrameworkNamespace(_testFramework)};");
        sb.AppendLine();
        sb.AppendLine($"namespace {testNamespace}");
        sb.AppendLine("{");
        if (GetClassAttribute(_testFramework) is { } classAttribute)
        {
            sb.AppendLine($"    {classAttribute}");
        }

        sb.AppendLine($"    public class {testClassName}");
        sb.AppendLine("    {");
        sb.AppendLine($"        {GetSkipAttribute(EscapeAttributeString(attributeReason))}");
        sb.AppendLine($"        public void {testMethodName}()");
        sb.AppendLine("        {");
        sb.AppendLine("            // Auto-generated skip stub by NetAI (Roslyn-only, no AI call).");

        foreach (var hintLine in hintLines)
        {
            if (string.IsNullOrWhiteSpace(hintLine))
            {
                continue;
            }

            foreach (var sub in hintLine.Split('\n'))
            {
                var trimmed = sub.TrimEnd();
                var withoutSlash = trimmed.StartsWith("//", StringComparison.Ordinal)
                    ? trimmed.Substring(2).TrimStart()
                    : trimmed;
                sb.AppendLine($"            // {withoutSlash}");
            }
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string EscapeAttributeString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Merges several test class snippets into one test class. The first snippet wins for class attributes, base types,
    /// fields and constructors (pass the existing file first). Additional types (helpers, fixtures) are preserved,
    /// and test methods whose names collide are renamed instead of being dropped silently.
    /// </summary>
    private string MergeCollectedTestClasses(string testClassName, string testNamespace, IReadOnlyList<string> classSnippets)
    {
        var usings = new List<UsingDirectiveSyntax>();
        var usingKeys = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<MemberDeclarationSyntax>();
        var seenMemberKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenMemberTexts = new HashSet<string>(StringComparer.Ordinal);
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        var otherTypes = new List<MemberDeclarationSyntax>();
        var otherTypeNames = new HashSet<string>(StringComparer.Ordinal);

        SyntaxList<AttributeListSyntax>? classAttributes = null;
        BaseListSyntax? baseList = null;
        SyntaxTokenList? modifiers = null;

        void AddUsing(UsingDirectiveSyntax directive)
        {
            var normalized = directive.WithoutTrivia();
            if (usingKeys.Add(GetUsingKey(normalized)))
            {
                usings.Add(normalized);
            }
        }

        AddUsing(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetTestFrameworkNamespace(_testFramework))));

        foreach (var snippet in classSnippets)
        {
            if (string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(snippet).GetCompilationUnitRoot();

            foreach (var u in root.Usings.Concat(root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings)))
            {
                AddUsing(u);
            }

            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax).ToList();

            var testClass = classes.FirstOrDefault(c => c.Members.OfType<MethodDeclarationSyntax>().Any(HasTestAttribute))
                            ?? classes.FirstOrDefault(c => c.Identifier.Text == testClassName)
                            ?? classes.FirstOrDefault(c => c.Members.OfType<MethodDeclarationSyntax>().Any());

            if (testClass is null)
            {
                continue;
            }

            if (classAttributes is null)
            {
                classAttributes = testClass.AttributeLists;
                baseList = testClass.BaseList;
                modifiers = testClass.Modifiers;
            }
            else if (baseList is null && testClass.BaseList is not null)
            {
                baseList = testClass.BaseList;
            }

            foreach (var member in testClass.Members)
            {
                var key = GetMemberKey(member);
                var cleaned = CleanTrivia(member);

                if (seenMemberKeys.Add(key))
                {
                    members.Add(cleaned);
                    if (member is MethodDeclarationSyntax added)
                    {
                        memberNames.Add(added.Identifier.Text);
                        seenMemberTexts.Add(added.NormalizeWhitespace().ToFullString());
                    }

                    continue;
                }

                // Same signature: identical test → skip; different test with the same name → keep it under a new name.
                if (member is MethodDeclarationSyntax method && HasTestAttribute(method) &&
                    seenMemberTexts.Add(method.NormalizeWhitespace().ToFullString()))
                {
                    var suffix = 2;
                    string newName;
                    do
                    {
                        newName = $"{method.Identifier.Text}_{suffix++}";
                    } while (memberNames.Contains(newName));

                    var renamed = ((MethodDeclarationSyntax)cleaned).WithIdentifier(SyntaxFactory.Identifier(newName));
                    memberNames.Add(newName);
                    seenMemberKeys.Add(GetMemberKey(renamed));
                    members.Add(renamed);
                }
            }

            foreach (var other in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                         .Where(t => t != testClass && t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax))
            {
                if (otherTypeNames.Add(other.Identifier.Text))
                {
                    otherTypes.Add(CleanTrivia(other));
                }
            }
        }

        var classDeclNew = SyntaxFactory.ClassDeclaration(testClassName)
            .WithModifiers(modifiers is { Count: > 0 } m ? m : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAttributeLists(classAttributes ?? default)
            .WithBaseList(baseList)
            .AddMembers(members.ToArray());

        if (classDeclNew.AttributeLists.Count == 0 && GetClassAttribute(_testFramework) is { } classAttribute)
        {
            classDeclNew = classDeclNew.AddAttributeLists(SyntaxFactory.ParseCompilationUnit($"{classAttribute} class C {{}}")
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().AttributeLists.ToArray());
        }

        // The test class must come first: TestProjectManager derives the file name from the first class.
        var namespaceDecl = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(testNamespace))
            .AddMembers(classDeclNew)
            .AddMembers(otherTypes.Where(t => !(t is BaseTypeDeclarationSyntax b && b.Identifier.Text == testClassName)).ToArray());

        var compilationUnit = SyntaxFactory.CompilationUnit().WithUsings(SyntaxFactory.List(usings)).AddMembers(namespaceDecl);

        return Formatter.Format(compilationUnit, _formatWorkspace).ToFullString();
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
        var parts = new List<string> { method.Identifier.Text, (method.TypeParameterList?.Parameters.Count ?? 0).ToString() };

        foreach (var p in method.ParameterList.Parameters)
        {
            var refKind = string.Empty;
            foreach (var modifier in p.Modifiers)
            {
                if (modifier.IsKind(SyntaxKind.RefKeyword) || modifier.IsKind(SyntaxKind.OutKeyword) ||
                    modifier.IsKind(SyntaxKind.InKeyword))
                {
                    refKind = modifier.ValueText;
                    break;
                }
            }

            parts.Add((p.Type?.WithoutTrivia().ToString() ?? string.Empty) + ":" + refKind);
        }

        return string.Join("|", parts);
    }

    private static MemberDeclarationSyntax CleanTrivia(MemberDeclarationSyntax member)
    {
        var originalLeading = member.GetLeadingTrivia();

        var preserved = new List<SyntaxTrivia>();
        foreach (var trivia in originalLeading)
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                preserved.Add(trivia);

                if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                {
                    preserved.Add(SyntaxFactory.EndOfLine("\n"));
                }
            }
            else if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                if (preserved.Count > 0 && preserved[^1].IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    preserved.Add(trivia);
                }
            }
        }

        return member.WithLeadingTrivia(SyntaxFactory.TriviaList(preserved)).WithTrailingTrivia();
    }

    private string BuildBasePrompt(string className, string methodName, string semanticHintXml, string projectContext, string classSkeleton,
        UnitTestSkeletonGenerator.GeneratorResult? skeleton, bool requiresSta)
    {
        var frameworkName = GetTestFrameworkName(_testFramework);
        var testAttribute = GetTestAttribute(_testFramework);
        var skipAttributeTemplate = GetSkipAttribute("<reason>");
        var mockFrameworkInstruction = _mockFramework == MockFramework.Unknown
            ? "Do not introduce a mocking library unless it is listed in <TestProject>."
            : $"When mocking is needed, use {_mockFramework} only if it is listed in <TestProject>.";

        var verbose = _config?.Frameworks?.VerbosePrompt ?? true;

        var sb = new StringBuilder();

        sb.AppendLine($"You are a .NET testing expert working with {frameworkName}.");
        sb.AppendLine($"Generate the test for method '{methodName}' in class '{className}' according to <SuggestedTestStrategy>.");
        sb.AppendLine($"- If the strategy is 'Generate' or 'Direct', produce one test method using {testAttribute}.");
        sb.AppendLine("- If the strategy is 'Skip', produce one test method with the framework's skip/ignore attribute " +
                      $"(e.g. {skipAttributeTemplate}); put the body in a comment only.");
        sb.AppendLine("- If the strategy is 'RefactorFirst', produce a Skip test AND list the required source refactorings as comments " +
                      "above the test. When the strategy includes a <SuggestedRefactoringPattern>, mirror it in the comments.");
        sb.AppendLine("- If the strategy is 'Reflection', produce one test method that invokes the private method via reflection. " +
                      "This strategy explicitly overrides the 'no reflection' rule below and applies only when the strategy says so.");
        sb.AppendLine();

        sb.AppendLine(
            "Use <SemanticAnalysis> as the source of truth for method and dependency facts, and follow <SuggestedTestStrategy> exactly.");
        sb.AppendLine("Do not use reflection, dynamic invocation, or workaround code for private/static/async-void members, " +
                      "unless <SuggestedTestStrategy> explicitly instructs it (see 'Reflection' strategy).");
        sb.AppendLine();

        sb.AppendLine("Rules for the test project:");
        sb.AppendLine("- You MAY create a new test class in the test project (naming: <ClassUnderTest>Tests).");
        sb.AppendLine("- Do NOT invent source types, members, project references, or NuGet packages.");
        sb.AppendLine("- You MAY use namespaces from referenced assemblies, from packages listed in <ProjectContext>, " +
                      "and from `using` directives shown in <SourceCode>. Do NOT invent new namespaces that are not derivable from these sources.");
        sb.AppendLine("- Do NOT invent types that are not present in <ProjectContext> or <SemanticAnalysis>.");
        sb.AppendLine("- Only call members that are listed inside <ContainingType>/<PublicMethods> or inside the <SourceCode> block. " +
                      "Never invent methods or properties on the subject under test.");
        sb.AppendLine("- `init`-only properties must be set via object initializer syntax: " +
                      "`new Foo { Bar = 1 }` (never `foo.Bar = 1` after construction).");
        sb.AppendLine($"- {mockFrameworkInstruction}");
        sb.AppendLine("- Use the selected test framework from <SelectedFrameworks>; the test project uses that framework's template.");
        sb.AppendLine("- Use a mocking library or helper only if it is listed in <TestProject>.");
        sb.AppendLine(
            "- Keep source-code refactoring advice separate from the generated test; do not modify or assume changes to the source project.");
        sb.AppendLine("- If the method under test returns Task or Task<T>, make the test method async Task. " +
                      "Do NOT make the test method async for 'async void' methods; those are covered by <SuggestedTestStrategy> (Skip/RefactorFirst).");
        sb.AppendLine("- If <SemanticAnalysis> contains <StaRequirement required=\"true\" />, the test must run on an STA thread. " +
                      "If the test project lists a compatible STA helper package (e.g. \"Xunit.StaFact\" for xUnit) under <PackageReferences>, " +
                      $"use its attribute (e.g. [StaFact]) instead of {testAttribute}. " +
                      $"Otherwise keep {testAttribute} and add a comment noting the STA requirement.");
        sb.AppendLine("- Name the test method following the pattern `MethodName_Scenario_ExpectedBehavior` " +
                      "(e.g. `ProcessOrder_EmptyCart_ThrowsInvalidOperationException`). " +
                      "For a Skip/RefactorFirst strategy, prefer a name like `MethodName_IsNotDirectlyTestable` or a similarly descriptive name.");
        sb.AppendLine("- The test project has <Nullable>enable</Nullable>. The generated test code must be nullable-correct: " +
                      "no non-nullable fields left uninitialized, no `null` assigned to non-nullable references, " +
                      "and no null-forgiving `!` operator unless the source already uses it in the same member.");
        sb.AppendLine();

        sb.AppendLine("Output format:");
        sb.AppendLine("- Return ONLY compilable C# code (no explanations, no prose, no TODO markers outside comments).");
        sb.AppendLine(
            "- \"Compilable C# code\" refers to the generated TEST code, assuming the source project\r\n  compiles as-is. Source compilation errors reported by the analyzer are host artifacts\r\n  and do not affect this assumption.");
        sb.AppendLine(
            $"- Include \"using {GetTestFrameworkNamespace(_testFramework)};\" at the top of the generated code UNLESS the test project's <ProjectContext> already lists that namespace under <GlobalUsings>.");
        sb.AppendLine("- Use top-level usings consistent with ImplicitUsings/Nullable settings from <ProjectContext>.");
        sb.AppendLine("- Use the test project's <RootNamespace> from <ProjectContext> verbatim when present.");
        sb.AppendLine(
            "- If <RootNamespace> is absent, derive the namespace from the test project file name (without the .csproj extension). Do not invent any other namespace.");
        sb.AppendLine($"- Include {testAttribute} (or the framework-specific attribute, including STA variants) exactly once.");
        sb.AppendLine();

        if (skeleton is { } sk && sk.Mode != UnitTestSkeletonGenerator.SkeletonMode.Fallback)
        {
            sb.AppendLine("=== BINDING TEST SKELETON ===");
            sb.AppendLine("The skeleton below is the authoritative structure. You MUST:");
            sb.AppendLine(
                "- Preserve the using directives, namespace, class name, field names, constructor and test attribute EXACTLY as shown.");
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
                        sb.AppendLine(
                            $"- `{sk.SutTypeName}` cannot be instantiated. Call `{sk.SutTypeName}.{sk.TargetMethodName}(...)` directly.");
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

    private async Task<SemanticHint> BuildSemanticHintAsync(RoslynDllTestabilityAnalyzer? analyzer, Compilation? compilation,
        MethodDeclarationSyntax methodDeclaration, GeneratorLogger? log, CancellationToken ct)
    {
        if (analyzer == null || compilation == null)
        {
            return EmptySemanticHint();
        }

        var methodName = methodDeclaration.Identifier.Text;

        try
        {
            var report = await analyzer.AnalyzeFromCompilationAsync(compilation, methodDeclaration, ct).ConfigureAwait(false);

            if (report.TestSkeleton is { } skel)
            {
                log.Info($"[NetAI] Skeleton mode for '{methodName}': {skel.Mode}, STA={skel.RequiresSta}, class={skel.TestClassName}");
            }

            if (!report.IsDirectlyTestable)
            {
                log.Info($"[NetAI] Blockers for '{methodName}': {string.Join(" | ", report.Blockers)}");
            }

            log.Info($"[NetAI] Semantic analysis for '{methodName}': {report.Verdict}");

            var filePath = methodDeclaration.SyntaxTree.FilePath ?? methodName;
            var xml = FormatReportAsXml(report, filePath, methodName);

            return new SemanticHint(
                xml,
                report.TestSkeleton,
                report.RequiresSta,
                report.Strategy,
                report.Blockers ?? Array.Empty<string>(),
                report.Verdict ?? string.Empty,
                report.SourceRefactoringRecommendations ?? new List<string>(),
                report.Method is { IsAsyncVoid: true } m
                    ? BuildAsyncVoidRefactoringPattern(m)
                    : Array.Empty<string>());
        }
        catch (InvalidOperationException ex)
        {
            log.Info($"[NetAI] Skipped semantic analysis for '{methodName}': {ex.Message}");
            return EmptySemanticHint();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warning($"[NetAI] Semantic analysis for '{methodName}' failed: {ex.Message}");
            return EmptySemanticHint();
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
                              $"isSealed=\"{ct.IsSealed}\" isAbstract=\"{ct.IsAbstract}\" mockable=\"{X(ct.Mockable)}\">");

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
                    sb.AppendLine("    <PublicCtors>");
                    foreach (var ctor in ct.Constructors)
                    {
                        sb.AppendLine($"      <Ctor signature=\"{X(ctor)}\" />");
                    }

                    sb.AppendLine("    </PublicCtors>");
                }
                else
                {
                    sb.AppendLine("    <PublicCtors>none</PublicCtors>");
                }

                if (ct.PublicMethods.Count > 0)
                {
                    sb.AppendLine("    <PublicMethods>");
                    foreach (var pm in ct.PublicMethods)
                    {
                        sb.AppendLine($"      <Method signature=\"{X(pm)}\" />");
                    }

                    sb.AppendLine("    </PublicMethods>");
                }

                sb.AppendLine("  </ContainingType>");
            }
        }

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
                              $"usages=\"{t.Usages}\">");

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
                    sb.AppendLine(
                        $"      <RecommendedAbstractionPackage>{X(t.RecommendedAbstractionPackage)}</RecommendedAbstractionPackage>");
                }

                if (!string.IsNullOrEmpty(t.RecommendationReason))
                {
                    sb.AppendLine($"      <Reason>{X(t.RecommendationReason)}</Reason>");
                }

                if (t.PublicMethods.Count > 0)
                {
                    sb.AppendLine("      <PublicMethods>");
                    foreach (var pm in t.PublicMethods.Take(20))
                    {
                        sb.AppendLine($"        <Method signature=\"{X(pm)}\" />");
                    }

                    sb.AppendLine("      </PublicMethods>");
                }

                sb.AppendLine("    </Dependency>");
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
                        sb.AppendLine(
                            "    <Instruction>Split the async void handler into a thin UI shim and a testable async Task, then write the test against the Task.</Instruction>");
                        sb.AppendLine(
                            "    <Fallback>Do not change production code. Keep the Skip attribute from the binding skeleton verbatim and describe the required refactoring only in a comment.</Fallback>");
                        sb.AppendLine("    <SuggestedRefactoringPattern>");
                        foreach (var line in BuildAsyncVoidRefactoringPattern(method))
                        {
                            sb.AppendLine($"      {X(line)}");
                        }

                        sb.AppendLine("    </SuggestedRefactoringPattern>");
                        sb.AppendLine(
                            "    <Constraint>Do not use reflection. Do not invoke the handler directly. Do not perform real static I/O.</Constraint>");
                        sb.AppendLine("  </SuggestedTestStrategy>");
                    }
                    else
                    {
                        sb.AppendLine($"  <SuggestedTestStrategy action=\"RefactorFirst\" testFramework=\"{frameworkName}\">");
                        sb.AppendLine(
                            $"    <Instruction>The method has blockers that cannot be safely worked around in a test: {X(string.Join(" | ", report.Blockers))}.</Instruction>");
                        sb.AppendLine(
                            "    <Fallback>Do not change production code. Keep the Skip attribute from the binding skeleton verbatim and describe the required refactoring only in a comment.</Fallback>");
                        sb.AppendLine("  </SuggestedTestStrategy>");
                    }

                    return;
                }

            case UnitTestSkeletonGenerator.TestStrategy.Reflection:
                sb.AppendLine($"  <SuggestedTestStrategy action=\"Reflection\" testFramework=\"{frameworkName}\">");
                sb.AppendLine(
                    "    <Instruction>Use reflection only to invoke this synchronous private method; use no invented dependencies.</Instruction>");
                sb.AppendLine("  </SuggestedTestStrategy>");
                return;

            case UnitTestSkeletonGenerator.TestStrategy.Skip:
                sb.AppendLine($"  <SuggestedTestStrategy action=\"Skip\" testFramework=\"{frameworkName}\">");
                sb.AppendLine("    <Instruction>Skip this method and explain why in a comment.</Instruction>");
                sb.AppendLine("  </SuggestedTestStrategy>");
                return;

            default:
                sb.AppendLine($"  <SuggestedTestStrategy action=\"Direct\" testFramework=\"{frameworkName}\">");
                sb.AppendLine(
                    "    <Instruction>Call the method through its declared accessible API and assert observable behavior.</Instruction>");
                sb.AppendLine("  </SuggestedTestStrategy>");
                return;
        }
    }

    private static IReadOnlyList<string> BuildAsyncVoidRefactoringPattern(MethodFact method)
    {
        var asyncName = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name + "Core" : method.Name + "Async";

        return new[]
        {
            "// BEFORE:", $"//   private async void {method.Name}(object sender, RoutedEventArgs e)",
            "//   { /* original async body */ }", "//", "// AFTER:",
            $"//   private async void {method.Name}(object sender, RoutedEventArgs e)", $"//       => await {asyncName}();", "//",
            $"//   private async Task {asyncName}()", "//   { /* original async body, now awaitable & testable */ }", "//",
            "// Rationale: the shim stays UI-bound by design and is excluded from unit tests;",
            $"// the {asyncName} method carries all logic and is fully unit-testable."
        };
    }

    private string GetSkipAttribute(string reason)
    {
        return _testFramework switch
        {
            // NUnit/MSTest: [Ignore] alone does not make a method a test, so the stub would never show up as skipped.
            TestFramework.NUnit => $"[Test, Ignore(\"{reason}\")]",
            TestFramework.MSTest => $"[TestMethod, Ignore(\"{reason}\")]",
            TestFramework.xUnit => $"[Fact(Skip = \"{reason}\")]",
            _ => throw new ArgumentOutOfRangeException(nameof(_testFramework), _testFramework, "Unsupported test framework.")
        };
    }

    private static string? GetClassAttribute(TestFramework testFramework)
    {
        return testFramework switch
        {
            TestFramework.MSTest => "[TestClass]",
            _ => null
        };
    }

    private string BuildProjectContextHint(string sourceFilePath, string testProjectDirectory, TestProjectInfo testProject)
    {
        var testFramework = _testFramework;
        var mockFramework = _mockFramework;
        var sb = new StringBuilder();
        sb.AppendLine("<ProjectContext>");

        var sourceProjectPath = FindProjectFile(sourceFilePath);
        var sourceDocument = sourceProjectPath is null ? null : TryLoadXml(sourceProjectPath);
        if (sourceProjectPath is not null && sourceDocument is not null)
        {
            AppendProjectContext(sb, "SourceProject", sourceProjectPath, sourceDocument);
        }
        else
        {
            sb.AppendLine("  <SourceProject status=\"not-found\" />");
        }

        if (testProject.ProjectFile is { } testProjectPath && testProject.Document is { } testDocument)
        {
            AppendProjectContext(sb, "TestProject", testProjectPath, testDocument);
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

    private static XDocument? TryLoadXml(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static void AppendProjectContext(StringBuilder sb, string elementName, string projectPath, XDocument doc)
    {
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

    private string PrepareValidationStructure(string testClassName, string? testNamespaceName, string methodCode,
        UnitTestSkeletonGenerator.GeneratorResult? skeleton = null)
    {
        // Use exactly the namespace the merged file is written with; otherwise code validated here may not compile there.
        var namespaceName = string.IsNullOrWhiteSpace(testNamespaceName) ? "NetAI.Generated.Tests" : testNamespaceName!;

        if (string.IsNullOrWhiteSpace(methodCode))
        {
            throw new InvalidOperationException("Generated code does not contain a test method.");
        }

        var generatedRoot = CSharpSyntaxTree.ParseText(methodCode).GetCompilationUnitRoot();

        List<MemberDeclarationSyntax> members;

        var incomingClass = generatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Members.OfType<MethodDeclarationSyntax>().Any(HasTestAttribute));

        if (incomingClass is not null)
        {
            members = incomingClass.Members.ToList();
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

                var wrappedRoot = CSharpSyntaxTree.ParseText($"class GeneratedTestContainer {{ {classMembers} }}")
                    .GetCompilationUnitRoot();
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

        void AddUsingIfMissing(string name)
        {
            var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(name));
            if (usingKeys.Add(GetUsingKey(directive)))
            {
                usings.Add(directive);
            }
        }

        if (skeleton?.TestSkeleton is { } skeletonCode)
        {
            var skeletonRoot = CSharpSyntaxTree.ParseText(skeletonCode).GetCompilationUnitRoot();
            foreach (var u in skeletonRoot.Usings)
            {
                var normalized = u.WithoutTrivia();
                if (usingKeys.Add(GetUsingKey(normalized)))
                {
                    usings.Add(normalized);
                }
            }
        }

        foreach (var usingDirective in generatedRoot.Usings)
        {
            var normalizedUsing = usingDirective.WithoutTrivia();
            if (usingKeys.Add(GetUsingKey(normalizedUsing)))
            {
                usings.Add(normalizedUsing);
            }
        }

        if (methodCode.Contains(".Should()", StringComparison.Ordinal)
            || methodCode.Contains(".Should(", StringComparison.Ordinal))
        {
            if (_config?.Frameworks?.UseFluentAssertions != false)
            {
                AddUsingIfMissing("FluentAssertions");
            }
        }

        if (_mockFramework == MockFramework.Moq
            && (methodCode.Contains("Mock<", StringComparison.Ordinal)
                || methodCode.Contains("Mock.", StringComparison.Ordinal)
                || methodCode.Contains("It.Is", StringComparison.Ordinal)))
        {
            AddUsingIfMissing("Moq");
        }

        if (_mockFramework == MockFramework.NSubstitute
            && methodCode.Contains("Substitute.", StringComparison.Ordinal))
        {
            AddUsingIfMissing("NSubstitute");
        }

        if (_mockFramework == MockFramework.FakeItEasy
            && methodCode.Contains("A.Fake", StringComparison.Ordinal))
        {
            AddUsingIfMissing("FakeItEasy");
        }

        if (_config?.Frameworks?.UseAutoFixture == true
            && (methodCode.Contains("new Fixture(", StringComparison.Ordinal)
                || methodCode.Contains("IFixture", StringComparison.Ordinal)))
        {
            AddUsingIfMissing("AutoFixture");
        }

        // Keep class-level attributes ([TestClass], [Collection]) and base types (IClassFixture<T>, IDisposable)
        // of the AI class, otherwise MSTest finds no tests and fixture constructors fail at run time.
        var generatedClass = SyntaxFactory.ClassDeclaration(testClassName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithAttributeLists(incomingClass?.AttributeLists ?? default)
            .WithBaseList(incomingClass?.BaseList)
            .AddMembers(members.ToArray());

        if (generatedClass.AttributeLists.Count == 0 && GetClassAttribute(_testFramework) is { } classAttribute)
        {
            generatedClass = generatedClass.AddAttributeLists(SyntaxFactory.ParseCompilationUnit($"{classAttribute} class C {{}}")
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().AttributeLists.ToArray());
        }

        var helperTypes = incomingClass is null
            ? Array.Empty<MemberDeclarationSyntax>()
            : generatedRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Where(t => t != incomingClass && t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
                .Cast<MemberDeclarationSyntax>().ToArray();

        var generatedNamespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName))
            .AddMembers(generatedClass)
            .AddMembers(helperTypes);

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usings))
            .AddMembers(generatedNamespace);

        return Formatter.Format(compilationUnit, _formatWorkspace).ToFullString();
    }

    private static string GetUsingKey(UsingDirectiveSyntax usingDirective)
    {
        var normalized = usingDirective.WithoutTrivia();
        return string.Join("|", normalized.GlobalKeyword.RawKind, normalized.StaticKeyword.RawKind, normalized.Alias?.Name.ToString(),
            normalized.Name?.ToString());
    }

    private static string BuildClassSkeleton(TypeDeclarationSyntax targetClass, MethodDeclarationSyntax targetMethod,
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

    /// <summary>Extracts the meaningful part of an AI response.</summary>
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

    private static bool HasTestAttribute(MethodDeclarationSyntax method)
    {
        foreach (var attributeList in method.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var name = attribute.Name.ToString();

                if (name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal) ||
                    name.EndsWith("Test", StringComparison.Ordinal) || name.EndsWith("TestCase", StringComparison.Ordinal) ||
                    name.EndsWith("TestMethod", StringComparison.Ordinal) || name.EndsWith("DataTestMethod", StringComparison.Ordinal) ||
                    name.EndsWith("StaFact", StringComparison.Ordinal) || name.EndsWith("STATestMethod", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? FindProjectDirectory(string filePath) =>
        FindProjectFile(filePath) is { } projectFile ? Path.GetDirectoryName(projectFile) : null;

    private static string? FindProjectFile(string filePath)
    {
        try
        {
            for (var currentDir = Path.GetDirectoryName(filePath); currentDir != null; currentDir = Path.GetDirectoryName(currentDir))
            {
                var csproj = Directory.EnumerateFiles(currentDir, "*.csproj").FirstOrDefault();
                if (csproj != null)
                {
                    return csproj;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable parent directory: treat as "no project found".
        }

        return null;
    }

    private static string? GetSourceNamespace(TypeDeclarationSyntax targetClass)
    {
        for (var current = targetClass.Parent; current is not null; current = current.Parent)
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

    private static string ResolveTestNamespace(TestProjectInfo testProject, TypeDeclarationSyntax targetClass)
    {
        if (!string.IsNullOrWhiteSpace(testProject.RootNamespace))
        {
            return testProject.RootNamespace!;
        }

        var fromSource = GetSourceNamespace(targetClass);
        if (!string.IsNullOrWhiteSpace(fromSource))
        {
            return fromSource!.EndsWith(".Tests", StringComparison.Ordinal) ? fromSource! : fromSource + ".Tests";
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

    private static TestFramework ParseTestFramework(AiTestingConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (config.Frameworks is not null && Enum.TryParse(config.Frameworks.TestFramework?.Trim(), true, out TestFramework framework) &&
            framework != TestFramework.Unknown && Enum.IsDefined(typeof(TestFramework), framework) &&
            !int.TryParse(config.Frameworks.TestFramework, out _))
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

        var value = config.Frameworks?.MockingFramework?.Trim();

        // "unknown"/"none"/empty all mean "no mocking library" (the CLI default config uses "unknown").
        if (string.IsNullOrEmpty(value) || value!.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return MockFramework.Unknown;
        }

        if (Enum.TryParse(value, true, out MockFramework framework) && Enum.IsDefined(typeof(MockFramework), framework) &&
            !int.TryParse(value, out _))
        {
            return framework;
        }

        throw new ArgumentException(
            $"Unsupported mocking framework '{value}'. Supported values are moq, nsubstitute, fakeiteasy, and none.",
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
    {
        return string.IsNullOrEmpty(code) ? code : AiAreaMarkerLineRegex.Replace(code, string.Empty);
    }

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
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression) ||
                                              b.IsKind(SyntaxKind.CoalesceExpression) => 1,
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

    private sealed record SemanticHint(
        string Xml,
        UnitTestSkeletonGenerator.GeneratorResult? Skeleton,
        bool RequiresSta,
        UnitTestSkeletonGenerator.TestStrategy Strategy,
        IReadOnlyList<string> Blockers,
        string Verdict,
        IReadOnlyList<string> SourceRefactoringRecommendations,
        IReadOnlyList<string> AsyncVoidRefactoringLines);

    private static SemanticHint EmptySemanticHint() => new(
        string.Empty,
        null,
        false,
        UnitTestSkeletonGenerator.TestStrategy.Direct,
        Array.Empty<string>(),
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>());
}
