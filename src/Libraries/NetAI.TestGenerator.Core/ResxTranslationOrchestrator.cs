using DotNet10TestGenerator;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core.Services;
using System.Text;
using System.Text.RegularExpressions;
using NetAI.TestGenerator.Core.Models.Enums;

namespace NetAI.TestGenerator.Core;


// !!!! Wenn du sicher weißt, dass ein bestimmtes Mock-Framework verwendet wird, übergib es explizit (z. B. MockFramework.NSubstitute) – das ist zuverlässiger als die Muster-Erkennung.


public class ResxTranslationOrchestrator
{
    private readonly HttpClient _httpClient;

    private readonly TestGeneratorService _testGeneratorService;
    private readonly TestCodeBeautifier _testCodeBeautifier = new();

    public ResxTranslationOrchestrator(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6000) };
        _testGeneratorService = new TestGeneratorService(); // Instanziierung
    }


    public async Task<string> ProcessProjectAsync(string sourceFilePath, string testProjectDirectory, Action<string>? logInfo = null)
    {
        try
        {
            sourceFilePath = Path.Combine(sourceFilePath, "MainWindow.xaml.cs");


            // Sicherheitsprüfung: Existiert die Quellcodedatei überhaupt?
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                return "Source file empty or missing.";
            }

            // 1. Quellklasse mit Roslyn parsen, um Namen und Methoden zu extrahieren
            string sourceCode = File.ReadAllText(sourceFilePath);
            var sourceRoot = CSharpSyntaxTree.ParseText(sourceCode).GetCompilationUnitRoot();
            var targetClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            if (targetClass == null)
            {
                return "No class found in source file.";
            }

            string className = targetClass.Identifier.Text;
            string testClassName = $"{className}Tests";
            string testFilePath = Path.Combine(testProjectDirectory, $"{testClassName}.cs");

            logInfo?.Invoke($"[NetAI] Starting analysis for class: {className}");

            // 2. LIVE-SCAN über den Service: Welche Tests existieren bereits auf der Festplatte?
            var existingTestMethods = _testGeneratorService.GetExistingTestMethods(testFilePath);
            var sourceMethods = targetClass.DescendantNodes().OfType<MethodDeclarationSyntax>();

            var localLlmClient = new LocalLlmClient();
            var aiPromptBuilderSimple = new AiPromptBuilderSimple();
            var manager = new TestProjectManager();

            // 3. Jede Methode der Quellklasse einzeln prüfen
            foreach (var method in sourceMethods)
            {
                string methodName = method.Identifier.Text;

                // Prüfen, ob der Methodenname bereits in einer der existierenden Testmethoden vorkommt
                bool testExists = existingTestMethods.Any(t => t.Contains(methodName, StringComparison.OrdinalIgnoreCase));

                if (testExists)
                {
                    logInfo?.Invoke($"[NetAI] Method '{methodName}' is already covered by an existing test. Skipping.");
                    continue;
                }

                logInfo?.Invoke($"[NetAI] Missing test detected for method: {methodName}. Triggering AI generation...");

                // 4. Prompt dynamisch für diese spezifische Methode aufbauen
                // Hier übergeben wir den exakten Code der ungetesteten Methode an die KI
                string basePrompt = $"Erstelle eine präzise, lauffähige xUnit Unit-Test-Methode (mit [Fact]) für die Methode '{methodName}' aus der Klasse '{className}'. Hier ist der Quellcode der Methode:\n\n{method.ToString()}";

                // Ersten Testentwurf von der KI anfordern
                var newTestClassResponse = await localLlmClient.AskAsync(basePrompt, "Du bist ein C#-Test-Experte. Antworte ausschließlich mit lauffähigem C#-Code ohne Erklärungen.");
                newTestClassResponse = newTestClassResponse.Replace("using NSubstitute;", "");

                // Reinen Methoden-Code (oder falls die KI fälschlicherweise eine Klasse generiert hat) isolieren
                string testMethodCode = ExtractTestClass(newTestClassResponse);

                // 5. Validierungs- und Selbstreparaturschleife (Maximal 3 Versuche)
                const int MaxRetries = 3;
                TestGenerationResult? result = null;
                bool isCompiledSuccessfully = false;

                int aiAttempts = 0;
                int envAttempts = 0;

                const int MaxAiRetries = 3;   // wie bisher: AI-Reparaturversuche
                const int MaxEnvRetries = 3;  // zusätzlich: Umgebungs-Retries (ohne AI)

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
                    // NEU: Roslyn-basiertes Beautify + automatische using-Ergänzung
                    //     (Test-Framework: xUnit, Mock-Framework wird aus dem Code erkannt)
                    // -----------------------------------------------------------------
                    validationClassStructure = await _testCodeBeautifier.BeautifyAndAddUsingsAsync(
                        validationClassStructure,
                        testFramework: TestFramework.xUnit,
                        mockFramework: MockFramework.Unknown); // Unknown ⇒ reine Muster-Erkennung

                    // Code gegen den echten C#-Compiler prüfen
                    result = await manager.SetupAndValidateTestAsync(sourceFilePath, validationClassStructure);

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
                        await Task.Delay(TimeSpan.FromSeconds(5));
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
                    if (result.CompilerErrors?.Any() == true)
                    {
                        var roslynFixed = await _testCodeBeautifier.TryFixCompilerErrorsAsync(
                            codeForRepair,
                            result.CompilerErrors);

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

                    var correctedOutput = await localLlmClient.AskAsync(errorPrompt, systemPrompt);

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
                    logInfo?.Invoke($"[NetAI] Error: Could not generate a compilable test for '{methodName}' after {MaxRetries} retries.");
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


    public static string ExtractTestClass(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
            return string.Empty;

        // 1. Markdown-Code-Blöcke (```csharp ... ```) entfernen, falls vorhanden
        var codeBlockRegex = new Regex(@"```(?:csharp|cs)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        var match = codeBlockRegex.Match(aiResponse);
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
}