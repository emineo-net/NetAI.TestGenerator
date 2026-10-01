using DotNet10TestGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Models.Enums;
using NetAI.TestGenerator.Core.Services;
using System.Text;
using System.Text.RegularExpressions;

namespace NetAI.TestGenerator.Core;

/// <summary>Coordinates source analysis, AI-generated unit tests, and optional compile validation.</summary>
public class ResxTranslationOrchestrator
{
    private static readonly Regex TestCodeBlockRegex = new(
        @"```(?:csharp|cs)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TestGeneratorService _testGeneratorService;
    private readonly TestCodeBeautifier _testCodeBeautifier = new();

    /// <summary>Creates an orchestrator for generating tests from source files.</summary>
    /// <param name="httpClient">Reserved for custom transport support; the current implementation uses <see cref="LocalLlmClient"/>.</param>
    public ResxTranslationOrchestrator(HttpClient? httpClient = null)
    {
        _testGeneratorService = new TestGeneratorService();
    }


    /// <summary>Generates tests for uncovered methods in the first class found in a source file.</summary>
    /// <param name="sourceFilePath">Path to the source file to analyze.</param>
    /// <param name="testProjectDirectory">Directory where generated test files and the test project are stored.</param>
    /// <param name="logInfo">Optional callback for progress and diagnostic messages.</param>
    /// <param name="compilation">Optional Roslyn compilation used to enrich prompts with semantic facts.</param>
    /// <param name="promptOnly">When <see langword="true"/>, skips compile validation after saving each generated test.</param>
    /// <returns><c>"ok"</c> when processing completes, or an error message if processing fails.</returns>
    public async Task<string> ProcessProjectAsync(
    string sourceFilePath,
    string testProjectDirectory,
    Action<string>? logInfo = null,
    Compilation? compilation = null,
    bool promptOnly = false)
    {
        try
        {
            bool tääästDebugger = true;

//#if DEBUG
//            if (tääästDebugger)
//            {
//                System.Diagnostics.Debugger.Launch();
//            }
//#endif

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

            var sourceRoot = CSharpSyntaxTree.ParseText(sourceCode).GetCompilationUnitRoot();
            var targetClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            if (targetClass == null)
            {
                return "No class found in source file.";
            }

            string className = targetClass.Identifier.Text;
            string? hostProjectDir = promptOnly ? FindProjectDirectory(sourceFilePath) : null;

            BuildLogger.BuildLog("\nhostProjectDir: " + hostProjectDir);

            string testClassName = $"{className}Tests";
            string testFilePath = Path.Combine(testProjectDirectory, $"{testClassName}.cs");

            BuildLogger.BuildLog("\ntestFilePath: " + testFilePath);

            logInfo?.Invoke($"[NetAI] Starting analysis for class: {className}");

            var existingTestMethods = _testGeneratorService.GetExistingTestMethods(testFilePath);
            var sourceMethods = targetClass.DescendantNodes().OfType<MethodDeclarationSyntax>();

            var localLlmClient = new LocalLlmClient();
            var aiPromptBuilderSimple = new AiPromptBuilderSimple();
            var testProjectManager = new TestProjectManager();

            var compilerService = new TestProjectManagerCompilerService(
                code => testProjectManager.SetupAndValidateTestAsync(
                    sourceFilePath, code, testProjectDirectoryOverride: testProjectDirectory));

            var testCodeProcessor = new TestCodeProcessor(compilerService);

            RoslynDllTestabilityAnalyzer? semanticAnalyzer = null;
            if (compilation != null)
            {
                semanticAnalyzer = new RoslynDllTestabilityAnalyzer();
                logInfo?.Invoke("[NetAI] Semantic analysis is available; AI prompts will include semantic context.");
            }
            else
            {
                logInfo?.Invoke("[NetAI] No compilation was provided; using syntax-only analysis.");
            }

            foreach (var method in sourceMethods)
            {
                var methodString = method.ToString();
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
                    semanticAnalyzer, compilation, sourceFilePath, methodName, logInfo).ConfigureAwait(false);


                BuildLogger.BuildLog("\nsemanticHint: " + semanticHint);

                string basePrompt =
                    $"You are a .NET testing expert. Create a precise xUnit test method (with [Fact]) for method '{methodName}' in class '{className}'.\n\n" +
                    "Carefully read the automatically generated semantic analysis in <Analysis>. " +
                    "If the 'Verdict' identifies constraints (such as 'private' or static dependencies), try to work around them pragmatically in the test " +
                    "(e.g. use reflection for private members or libraries such as 'System.IO.Abstractions' if mentioned in the recommendations). " +
                    "ALWAYS return an xUnit test method. If a runnable test is technically impossible (e.g. for 'async void' or untestable code), still create a test method with [Fact(Skip = \"<brief reason>\")] and include the problematic code only as a comment or block comment in the body.\n\n" +
                    $"<Analysis>\n{semanticHint}\n</Analysis>\n\n" +
                    $"Relevant context (usings, fields, method under test, and called helper methods):\n\n" +
                    $"<SourceCode>\n{classSkeleton}\n</SourceCode>";

                BuildLogger.BuildLog("\nbasePrompt: " + basePrompt);

                if (promptOnly)
                {
                    string promptDirectory = Path.Combine(hostProjectDir ?? Path.GetDirectoryName(sourceFilePath)!, "obj", "netai", "prompts");
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


                    BuildLogger.BuildLog("\nresultErrors: " + string.Join("\n", result.CompilerErrors.ToList()));

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
                            codeForRepair,
                            result.CompilerErrors).ConfigureAwait(false);

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
                        _testGeneratorService.CreateNewTestClassFile(testFilePath, testClassName, targetClass.Parent as NamespaceDeclarationSyntax, testMethodCode);
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
                    {
                        logInfo?.Invoke($"[NetAI] Final Compiler Errors:\n{string.Join("\n", result.CompilerErrors)}");
                    }
                }
            }

            return "ok";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static async Task<string> BuildSemanticHintAsync(
        RoslynDllTestabilityAnalyzer? analyzer,
        Compilation? compilation,
        string sourceFilePath,
        string methodName,
        Action<string>? logInfo)
    {
        if (analyzer == null || compilation == null)
            return string.Empty;

        try
        {
            var report = await analyzer.AnalyzeFromCompilationAsync(
                compilation,
                methodName,
                documentName: Path.GetFileName(sourceFilePath));

            var sb = new StringBuilder();
            sb.AppendLine("// --- Semantic analysis ---");
            sb.AppendLine($"// Verdict: {report.Verdict}");

            if (report.Method?.ContainingType != null)
            {
                var ct = report.Method.ContainingType;
                sb.AppendLine($"// ContainingType: {ct.FullName}");
                sb.AppendLine($"// Mockable: {ct.Mockable}");
                sb.AppendLine($"// Interfaces: {string.Join(", ", ct.Interfaces)}");
                sb.AppendLine($"// Public Ctors: {string.Join(" | ", ct.Constructors)}");
            }

            var staticDeps = report.ReferencedTypes?
                .Where(t => t.UsedStatically && !string.IsNullOrWhiteSpace(t.FullName)) 
                .Select(t => t.FullName)
                .ToList() ?? new List<string>();

            if (staticDeps.Count > 0)
            {
                sb.AppendLine($"// Static dependencies: {string.Join(", ", staticDeps)}");
                sb.AppendLine("// Note: These cannot be mocked; consider working around them in the test via " +
                              "System.IO.Abstractions or a wrapper.");
            }

            var injectable = report.ReferencedTypes?
                .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsStatic && !t.IsSealed
                            && !string.IsNullOrWhiteSpace(t.FullName)
                            && !t.Namespace.StartsWith("System", StringComparison.Ordinal))
                .Select(t => t.FullName)
                .ToList() ?? new List<string>();

            if (injectable.Count > 0)
            {
                sb.AppendLine($"// Concrete types (prefer constructor injection): " +
                              $"{string.Join(", ", injectable)}");
            }

            if (report.Recommendations?.Count > 0)
            {
                sb.AppendLine("// Recommendations:");
                foreach (var rec in report.Recommendations)
                    sb.AppendLine($"//   - {rec}");
            }

            if (report.CompilationErrors?.Count > 0)
            {
                sb.AppendLine($"// Compilation errors: {report.CompilationErrors.Count}");
                foreach (var err in report.CompilationErrors.Take(3))
                    sb.AppendLine($"//   {err}");
            }

            logInfo?.Invoke($"[NetAI] Semantic analysis for '{methodName}': {report.Verdict}");
            return sb.ToString();
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

    private string PrepareValidationStructure(string testClassName, NamespaceDeclarationSyntax? originalNamespace, string methodCode)
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

        var usings = targetClass.SyntaxTree
            .GetCompilationUnitRoot()
            .Usings;

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
    /// <param name="aiResponse">Raw response returned by the model.</param>
    /// <returns>Extracted method declarations, or the trimmed response when no methods are found.</returns>
    public static string ExtractTestClass(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
            return string.Empty;

        var match = TestCodeBlockRegex.Match(aiResponse);
        string rawCode = match.Success ? match.Groups[1].Value : aiResponse;

        var tree = CSharpSyntaxTree.ParseText(rawCode);
        var root = tree.GetCompilationUnitRoot();

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        if (!methods.Any())
        {
            return rawCode.Trim();
        }

        var sb = new StringBuilder();
        foreach (var method in methods)
        {
            sb.AppendLine(method.ToFullString());
        }

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

}