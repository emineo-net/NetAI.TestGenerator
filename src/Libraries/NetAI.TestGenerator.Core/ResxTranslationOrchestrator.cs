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


// !!!! Wenn du sicher weißt, dass ein bestimmtes Mock-Framework verwendet wird, übergib es explizit (z. B. MockFramework.NSubstitute) – das ist zuverlässiger als die Muster-Erkennung.


public class ResxTranslationOrchestrator
{
    private static readonly Regex TestCodeBlockRegex = new(
        @"```(?:csharp|cs)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TestGeneratorService _testGeneratorService;
    private readonly TestCodeBeautifier _testCodeBeautifier = new();

    public ResxTranslationOrchestrator(HttpClient? httpClient = null)
    {
        _testGeneratorService = new TestGeneratorService(); // Instanziierung
    }


    /// <summary>
    /// Verarbeitet eine Quelldatei: prüft für jede Methode, ob bereits ein Test existiert,
    /// und generiert bei Bedarf per KI einen neuen Test.
    ///
    /// Wenn eine <paramref name="compilation"/> übergeben wird, wird zusätzlich der
    /// semantische Testbarkeits-Analyzer genutzt, um den KI-Prompt anzureichern
    /// (Mockability, statische Abhängigkeiten, Konstruktoren, Empfehlungen).
    /// Ohne Compilation bleibt das Verhalten rein syntaktisch.
    /// </summary>
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

            // Sicherheitsprüfung: Existiert die Quellcodedatei überhaupt?
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                return "Source file empty or missing.";
            }

            // NEU & STABIL: Datei asynchron und mit FileShare lesen (behebt WPF/MSBuild-Sperren)
            string sourceCode;
            using (var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                sourceCode = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            // 1. Quellklasse mit Roslyn parsen, um Namen und Methoden zu extrahieren
            var sourceRoot = CSharpSyntaxTree.ParseText(sourceCode).GetCompilationUnitRoot();
            var targetClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            if (targetClass == null)
            {
                return "No class found in source file.";
            }

            string className = targetClass.Identifier.Text;
            string? hostProjectDir = promptOnly ? FindProjectDirectory(sourceFilePath) : null;

            string testClassName = $"{className}Tests";
            string testFilePath = Path.Combine(testProjectDirectory, $"{testClassName}.cs");

            logInfo?.Invoke($"[NetAI] Starting analysis for class: {className}");

            // 2. LIVE-SCAN über den Service: Welche Tests existieren bereits auf der Festplatte?
            var existingTestMethods = _testGeneratorService.GetExistingTestMethods(testFilePath);
            var sourceMethods = targetClass.DescendantNodes().OfType<MethodDeclarationSyntax>();

            var localLlmClient = new LocalLlmClient();
            var aiPromptBuilderSimple = new AiPromptBuilderSimple();
            var testProjectManager = new TestProjectManager();

            // -----------------------------------------------------------------
            // NEU: Compiler-Service-Adapter + TestCodeProcessor
            // -----------------------------------------------------------------
            var compilerService = new TestProjectManagerCompilerService(
                code => testProjectManager.SetupAndValidateTestAsync(sourceFilePath, code));

            var testCodeProcessor = new TestCodeProcessor(compilerService);

            // -----------------------------------------------------------------
            // NEU: Semantische Analyse vorbereiten.
            // -----------------------------------------------------------------
            RoslynDllTestabilityAnalyzer? semanticAnalyzer = null;
            if (compilation != null)
            {
                semanticAnalyzer = new RoslynDllTestabilityAnalyzer();
                logInfo?.Invoke("[NetAI] Semantische Analyse verfügbar – KI-Prompts werden angereichert.");
            }
            else
            {
                logInfo?.Invoke("[NetAI] Keine Compilation übergeben – arbeite rein syntaktisch.");
            }

            // 3. Jede Methode der Quellklasse einzeln prüfen
            foreach (var method in sourceMethods)
            {
                var methodString = method.ToString();
                string methodName = method.Identifier.Text;

                // Prüfen, ob der Methodenname bereits in einer der existierenden Testmethoden vorkommt
                bool testExists = existingTestMethods.Any(t => t.Contains(methodName, StringComparison.OrdinalIgnoreCase));

                if (testExists)
                {
                    logInfo?.Invoke($"[NetAI] Method '{methodName}' is already covered by an existing test. Skipping.");
                    continue;
                }

                logInfo?.Invoke($"[NetAI] Missing test detected for method: {methodName}. Triggering AI generation...");

                // 4. Klassen-Skelett (Felder + Methode + genutzte Hilfsmethoden) als Kontext
                string classSkeleton = BuildClassSkeleton(targetClass, method);

                // Richtig platziertes ConfigureAwait!
                string semanticHint = await BuildSemanticHintAsync(
                    semanticAnalyzer, compilation, sourceFilePath, methodName, logInfo).ConfigureAwait(false);

                string basePrompt =
                    $"Du bist ein .NET-Test-Experte. Erstelle eine präzise xUnit-Testmethode (mit [Fact]) für die Methode '{methodName}' aus der Klasse '{className}'.\n\n" +
                    "Lies dazu die <Analyse>, automatisch generierte semantische Analyse genau aus. " +
                    "Falls das 'Verdict' Einschränkungen (wie 'private' oder statische Abhängigkeiten) aufzeigt, versuche diese im Test pragmatisch zu umgehen " +
                    "(z. B. via Reflection für private Member oder durch Nutzung von Bibliotheken wie 'System.IO.Abstractions', falls in den Hinweisen erwähnt). " +
                     "Gib IMMER eine xUnit-Testmethode zurück. Falls ein lauffähiger Test technisch unmöglich ist (z. B. bei 'async void' oder nicht testbarem Code), erstelle trotzdem eine Testmethode mit [Fact(Skip = \"<kurze Begründung>\")] und füge den problematischen Code nur als Kommentar oder Block-Kommentar im Body ein.\n\n" +
                    $"<Analyse>\n{semanticHint}\n</Analyse>\n\n" +
                    $"Relevanter Kontext (Usings, Felder, zu testende Methode, aufgerufene Hilfsmethoden):\n\n" +
                    $"<Quellcode>\n{classSkeleton}\n</Quellcode>";

                if (promptOnly)
                {
                    string promptDirectory = Path.Combine(hostProjectDir ?? Path.GetDirectoryName(sourceFilePath)!, "obj", "netai", "prompts");
                    Directory.CreateDirectory(promptDirectory);
                    string promptPath = Path.Combine(promptDirectory, $"{className}.{methodName}.prompt.md");
                    File.WriteAllText(promptPath, basePrompt, Encoding.UTF8);
                    logInfo?.Invoke($"[NetAI] Prompt mit semantischem Kontext gespeichert: {promptPath}");
                }

                // Richtig platziertes ConfigureAwait!
                var newTestClassResponse = await localLlmClient.AskAsync(
                    basePrompt,
                    "Du bist ein C#-Test-Experte. Antworte ausschließlich mit lauffähigem C#-Code ohne Erklärungen.").ConfigureAwait(false);

                // Reinen Methoden-Code isolieren
                string testMethodCode = ExtractTestClass(newTestClassResponse);

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
                    logInfo?.Invoke($"[NetAI] Testentwurf für '{methodName}' gespeichert; Compile-Validierung übersprungen.");
                    continue;
                }

             

                TestGenerationResult? result = null;
                bool isCompiledSuccessfully = false;

                int aiAttempts = 0;
                int envAttempts = 0;

                const int MaxAiRetries = 2;   // wie bisher: AI-Reparaturversuche
                const int MaxEnvRetries = 2;  // zusätzlich: Umgebungs-Retries (ohne AI)

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

                    // -----------------------------------------------------------------
                    // NEU: TestCodeProcessor übernimmt
                    //   - Usings ergänzen (Muster-Erkennung + Framework)
                    //   - Usings sortieren
                    //   - Formatieren
                    //   - Compile + automatisches Fixen fehlender usings (TryFixMissingUsing)
                    // -----------------------------------------------------------------
                    validationClassStructure = await testCodeProcessor.ProcessTestClassAsync(
                        validationClassStructure,
                        testFramework: TestFramework.xUnit,
                        mockFramework: MockFramework.Unknown).ConfigureAwait(false); // Unknown ⇒ reine Muster-Erkennung

                    // Code gegen den echten C#-Compiler prüfen
                    result = await testProjectManager.SetupAndValidateTestAsync(sourceFilePath, validationClassStructure).ConfigureAwait(false);

                    // -----------------------------------------------------------------
                    // 1) Erfolg
                    // -----------------------------------------------------------------
                    if (result.IsSuccess)
                    {
                        isCompiledSuccessfully = true;

                        // Wichtig: der Manager kann Usings ergänzt oder andere kleine Fixes
                        // gemacht haben. Wenn ja, ist result.TestClassCode der tatsächlich
                        // geschriebene Code – den übernehmen wir als neuen Stand.
                        if (!string.IsNullOrEmpty(result.TestClassCode))
                        {
                            testMethodCode = ExtractTestClass(result.TestClassCode!);
                        }
                        break;
                    }

                    // -----------------------------------------------------------------
                    // 2) Umgebungsproblem (z. B. gesperrte DLL durch VS oder laufende App)
                    //    → KEIN AI-Repair. Der generierte Code ist in Ordnung.
                    // -----------------------------------------------------------------
                    if (result.IsEnvironmentIssue)
                    {
                        envAttempts++;
                        logInfo?.Invoke($"[NetAI] Environment issue while validating '{methodName}' " +
                                        $"(env retry {envAttempts}/{MaxEnvRetries}). " +
                                        $"Not asking the AI to repair – the test code itself is fine. " +
                                        $"Cause: a referenced assembly is locked by another process.");

                        // Kurz warten und denselben Code nochmal durchlaufen lassen.
                        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        continue;
                    }

                    // -----------------------------------------------------------------
                    // 3) Echter Compilerfehler → AI-Reparatur
                    // -----------------------------------------------------------------
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

                    // Der Manager kann den Code verändert haben (z. B. Usings ergänzt).
                    // Wenn ja, diesen Stand als Basis nehmen – sonst das ursprüngliche Gerüst.
                    var codeForRepair = result.TestClassCode ?? validationClassStructure;

                    // Erst Roslyn versuchen zu lassen, triviale Fehler (fehlende usings) zu fixen,
                    // damit die AI sich auf echte inhaltliche Fehler konzentrieren kann.
                    // Hinweis: der TestCodeProcessor hat bereits versucht, usings zu fixen –
                    // hier werden zusätzlich die vom Manager gemeldeten Fehler adressiert.
                    if (result.CompilerErrors?.Any() == true)
                    {
                        var roslynFixed = await _testCodeBeautifier.TryFixCompilerErrorsAsync(
                            codeForRepair,
                            result.CompilerErrors).ConfigureAwait(false);

                        if (!string.Equals(roslynFixed, codeForRepair, StringComparison.Ordinal))
                        {
                            logInfo?.Invoke("[NetAI] Roslyn hat fehlende usings automatisch ergänzt – " +
                                            "erneuter Compile-Versuch ohne AI.");

                            testMethodCode = ExtractTestClass(roslynFixed);
                            continue; // zurück in die while-Schleife → erneute Validierung
                        }
                    }



                    var errorPrompt = aiPromptBuilderSimple.FixUnittestPromptSimple(errorsText, codeForRepair);

                    var systemPrompt = "Du bist ein präziser C#-Compiler-Assistent. Deine einzige Aufgabe ist es, " +
                                       "Syntax- und Kompilierfehler in bereitgestelltem C#-Code exakt zu reparieren " +
                                       "und lauffähigen Code ohne Text-Erklärungen zurückzugeben.\n";

                    var correctedOutput = await localLlmClient.AskAsync(errorPrompt, systemPrompt).ConfigureAwait(false);

                    // Korrigierten Code wieder extrahieren
                    testMethodCode = ExtractTestClass(correctedOutput);
                }

                // 6. Ergebnis verarbeiten: Bei Erfolg wegschreiben (Variante 2)
                if (isCompiledSuccessfully)
                {
                    if (!File.Exists(testFilePath))
                    {
                        // Fall A: Testdatei existiert noch gar nicht -> Neu anlegen mit Grundgerüst
                        _testGeneratorService.CreateNewTestClassFile(testFilePath, testClassName, targetClass.Parent as NamespaceDeclarationSyntax, testMethodCode);
                        logInfo?.Invoke($"[NetAI] Successfully created new test file and added '{methodName}_GeneratedTest'.");
                    }
                    else
                    {
                        // Fall B: Testdatei existiert bereits -> Die neue Methode sauber per Roslyn unten anhängen
                        _testGeneratorService.AppendMethodToExistingClassFile(testFilePath, testMethodCode);
                        logInfo?.Invoke($"[NetAI] Successfully appended test for '{methodName}' to existing test file.");
                    }

                    // Lokalen Cache aktualisieren, damit Folgemethoden in derselben Schleife Bescheid wissen
                    existingTestMethods.Add(methodName);
                }
                else
                {
                    logInfo?.Invoke($"[NetAI] Error: Could not generate a compilable test for '{methodName}' after {MaxAiRetries} retries.");
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

    // =====================================================================
    //  Semantischer Kontext für den KI-Prompt
    // =====================================================================

    /// <summary>
    /// Liefert einen Kommentar-Block mit semantischen Informationen zur Methode,
    /// der dem KI-Prompt vorangestellt wird. Wenn kein Analyzer / keine Compilation
    /// vorhanden ist, wird ein leerer String zurückgegeben.
    /// </summary>
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
            sb.AppendLine("// --- Semantische Analyse ---");
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
                sb.AppendLine($"// Statische Abhängigkeiten: {string.Join(", ", staticDeps)}");
                sb.AppendLine("// Hinweis: Diese sind nicht mockbar – im Test ggf. via " +
                              "System.IO.Abstractions oder Wrapper umgehen.");
            }

            var injectable = report.ReferencedTypes?
                .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsStatic && !t.IsSealed
                            && !string.IsNullOrWhiteSpace(t.FullName)
                            && !t.Namespace.StartsWith("System", StringComparison.Ordinal))
                .Select(t => t.FullName)
                .ToList() ?? new List<string>();

            if (injectable.Count > 0)
            {
                sb.AppendLine($"// Konkrete Typen (besser per Konstruktor injizieren): " +
                              $"{string.Join(", ", injectable)}");
            }

            if (report.Recommendations?.Count > 0)
            {
                sb.AppendLine("// Empfehlungen:");
                foreach (var rec in report.Recommendations)
                    sb.AppendLine($"//   - {rec}");
            }

            if (report.CompilationErrors?.Count > 0)
            {
                sb.AppendLine($"// Compiler-Fehler in der Compilation: {report.CompilationErrors.Count}");
                foreach (var err in report.CompilationErrors.Take(3))
                    sb.AppendLine($"//   {err}");
            }

            logInfo?.Invoke($"[NetAI] Semantik für '{methodName}': {report.Verdict}");
            return sb.ToString();
        }
        catch (InvalidOperationException ex)
        {
            // Methode evtl. nicht im SyntaxTree gefunden – kein Beinbruch.
            logInfo?.Invoke($"[NetAI] Semantik für '{methodName}' übersprungen: {ex.Message}");
            return string.Empty;
        }
        catch (Exception ex)
        {
            logInfo?.Invoke($"[NetAI] Semantik für '{methodName}' fehlgeschlagen: {ex.Message}");
            return string.Empty;
        }
    }

    // =====================================================================
    //  Bestehende Hilfsmethoden (unverändert)
    // =====================================================================

    // Kleine Hilfsmethode, um den Code für die temporäre Compiler-Prüfung zu wrappen
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

        // --- 0) Usings der Quelldatei (sehr wertvoll für die KI) ---
        var usings = targetClass.SyntaxTree
            .GetCompilationUnitRoot()
            .Usings;

        if (usings.Any())
        {
            foreach (var u in usings)
                sb.AppendLine(u.ToFullString().TrimEnd());
            sb.AppendLine();
        }

        // --- 1) Felder ---
        foreach (var field in targetClass.Members.OfType<FieldDeclarationSyntax>())
            sb.AppendLine(field.ToFullString().TrimEnd());

        // --- 2) Properties (optional) ---
        if (includeProperties)
            foreach (var prop in targetClass.Members.OfType<PropertyDeclarationSyntax>())
                sb.AppendLine(prop.ToFullString().TrimEnd());

        // --- 3) Konstruktoren (optional, oft wichtig für Setup) ---
        if (includeConstructors)
            foreach (var ctor in targetClass.Members.OfType<ConstructorDeclarationSyntax>())
                sb.AppendLine(ctor.ToFullString().TrimEnd());

        // --- 4) Zu testende Methode ---
        sb.AppendLine();
        sb.AppendLine(targetMethod.ToFullString().TrimEnd());

        // --- 5) Rekursiv aufgerufene Hilfsmethoden ---
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



    /// <summary>
    /// Liefert die Namen aller Methoden, die innerhalb von <paramref name="method"/> aufgerufen werden.
    /// Erkennt sowohl <c>Foo()</c> als auch <c>this.Foo()</c> / <c>obj.Foo()</c>.
    /// </summary>
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

    public static string ExtractTestClass(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
            return string.Empty;

        // 1. Markdown-Code-Blöcke (```csharp ... ```) entfernen, falls vorhanden
        var match = TestCodeBlockRegex.Match(aiResponse);
        string rawCode = match.Success ? match.Groups[1].Value : aiResponse;

        // 2. Die Antwort mit Roslyn als Syntaxbaum parsen
        var tree = CSharpSyntaxTree.ParseText(rawCode);
        var root = tree.GetCompilationUnitRoot();

        // 3. Alle Methoden-Deklarationen (z.B. [Fact] public void...) im Baum finden
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        if (!methods.Any())
        {
            // Fallback: Falls die KI absolut keine valide C#-Methode geliefert hat,
            // geben wir den getrimmten Rohtext zurück, damit der Compiler-Check anschlagen kann.
            return rawCode.Trim();
        }

        // 4. Alle gefundenen Testmethoden sauber aneinanderreihen
        var sb = new StringBuilder();
        foreach (var method in methods)
        {
            // .ToFullString() behält die Attribute (wie [Fact]) und XML-Kommentare bei
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
                // Suchen nach einer beliebigen .csproj im aktuellen Ordner
                if (Directory.EnumerateFiles(currentDir, "*.csproj").Any())
                {
                    return currentDir;
                }
                currentDir = Directory.GetParent(currentDir)?.FullName;
            }
        }
        catch
        {
            // Falls Zugriffsfehler auftreten
        }
        return null;
    }

}