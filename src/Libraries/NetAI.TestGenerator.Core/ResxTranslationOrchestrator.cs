using DotNet10TestGenerator;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models;
using NetAI.TestGenerator.Core.Services;
using System.Text;
using System.Text.RegularExpressions;

namespace NetAI.TestGenerator.Core;

public class ResxTranslationOrchestrator
{
    private readonly HttpClient _httpClient;

    private readonly TestGeneratorService _testGeneratorService;

    public ResxTranslationOrchestrator(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6000) };
        _testGeneratorService = new TestGeneratorService(); // Instanziierung
    }


    //public async Task<string> ProcessProject(string projectDir, string prompt, Action<string>? logInfo = null)
    //{
    //    try
    //    {
    //        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
    //        {
    //            return "empty";
    //        }

    //        logInfo?.Invoke($"[AI-Translator] Starting analysis in directory: {projectDir}");




    //        var requestData = new ApiTranslationRequest
    //        {
    //            TargetLanguage = "it",
    //            //Items = missingTranslations.Select(entry => new ApiTranslationItem
    //            //{
    //            //    Key = entry.Key,
    //            //    SourceText = FindSourceTextForKey(entry.Key, currentFile, parsedResources)
    //            //}).ToList()
    //        };


    //        var localLlmClient = new LocalLlmClient();


    //        var testpath = @"C:\temp\__trash\Textdokument.cs";

    //        if (File.Exists(testpath + "xxx"))
    //        {
    //            //             


    //            var sourceFilePath =
    //                @"C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Core\LocalLlmClient.cs";
    //            var testClassWithMethods = File.ReadAllText(testpath);

    //            var manager = new TestProjectManager();
    //            var result = await manager.SetupAndValidateTestAsync(sourceFilePath, testClassWithMethods);


    //            var erors = string.Join("\n", result.CompilerErrors.ToList());

    //            if (!string.IsNullOrEmpty(erors))
    //            {
    //                var aiPromptBuilderSimple = new AiPromptBuilderSimple();


    //                var errorPrompt = aiPromptBuilderSimple.FixUnittestPrompt(testClassWithMethods, erors);
    //                var newTestClass = await localLlmClient.AskAsync(errorPrompt, "Du bist ein präziser C#-Compiler-Assistent. Deine einzige Aufgabe ist es, Syntax- und Kompilierfehler in bereitgestelltem C#-Code exakt zu reparieren und lauffähigen Code ohne Text-Erklärungen zurückzugeben.\n");
    //            }

    //            // TODO: replace testclass in prompt an ask ai agein.

    //            // var newTestClass = await localLlmClient.AskAsync(prompt, "");

    //            //  var apiResponse = await CallTranslationApi(requestData);

    //            return "ok .....";
    //        }
    //        else
    //        {





    //            //var newTestClass = await localLlmClient.AskAsync(prompt, "");

    //            //newTestClass = newTestClass.Replace("using NSubstitute;", "");

    //            //var sourceFilePath = @"C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Core\LocalLlmClient.cs";
    //            //var testClassWithMethods = ExtractTestClass(newTestClass);


    //            //var manager = new TestProjectManager();
    //            //var result = await manager.SetupAndValidateTestAsync(sourceFilePath, testClassWithMethods);

    //            //if (result.CompilerErrors.Any(i => i.Contains("Could not automatically resolve a NuGet package for the namespace(s)")))
    //            //{

    //            //}
    //            //var aiPromptBuilderSimple = new AiPromptBuilderSimple();

    //            //var erors = string.Join("\n", result.CompilerErrors.ToList());

    //            //var errorPrompt = aiPromptBuilderSimple.FixUnittestPromptSimple(erors);
    //            //var newTestClass2 = await localLlmClient.AskAsync(errorPrompt, "Du bist ein präziser C#-Compiler-Assistent. Deine einzige Aufgabe ist es, Syntax- und Kompilierfehler in bereitgestelltem C#-Code exakt zu reparieren und lauffähigen Code ohne Text-Erklärungen zurückzugeben.\n");


    //            //*********

    //            var sourceFilePath = @"C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Core\LocalLlmClient.cs";
    //            var aiPromptBuilderSimple = new AiPromptBuilderSimple();
    //            var manager = new TestProjectManager();

    //            // 1. Ersten Testentwurf generieren
    //            var newTestClass = await localLlmClient.AskAsync(prompt, "");
    //            newTestClass = newTestClass.Replace("using NSubstitute;", "");
    //            var testClassWithMethods = ExtractTestClass(newTestClass);

    //            // 2. Validierungsschleife (Maximal 3 Reparaturversuche)
    //            const int MaxRetries = 3;
    //            TestGenerationResult result = null;

    //            for (int attempt = 1; attempt <= MaxRetries; attempt++)
    //            {
    //                // Testcode in das Testprojekt schreiben und kompilieren
    //                result = await manager.SetupAndValidateTestAsync(sourceFilePath, testClassWithMethods);

    //                // Wenn keine Compiler-Fehler mehr existieren, ist das Ziel erreicht
    //                if (result.CompilerErrors == null || !result.CompilerErrors.Any())
    //                {
    //                    break;
    //                }

    //                // Sonderfall abfangen (falls gewünscht)
    //                if (result.CompilerErrors.Any(i => i.Contains("Could not automatically resolve a NuGet package for the namespace(s)")))
    //                {
    //                    // Hier ggf. Logik einbauen oder mitsenden
    //                }

    //                // Fehler für das LLM aufbereiten
    //                var errorsText = string.Join("\n", result.CompilerErrors);
    //                var errorPrompt = aiPromptBuilderSimple.FixUnittestPromptSimple(errorsText, testClassWithMethods);

    //                var systemPrompt = "Du bist ein präziser C#-Compiler-Assistent. Deine einzige Aufgabe ist es, " +
    //                                   "Syntax- und Kompilierfehler in bereitgestelltem C#-Code exakt zu reparieren " +
    //                                   "und lauffähigen Code ohne Text-Erklärungen zurückzugeben.\n";

    //                // LLM um Korrektur bitten (nutzt den optimierten Prompt ohne Erklärungen)
    //                var correctedOutput = await localLlmClient.AskAsync(errorPrompt, systemPrompt);

    //                // Code wieder extrahieren für den nächsten Schleifendurchlauf
    //                testClassWithMethods = ExtractTestClass(correctedOutput);
    //            }

    //            // 3. Nach der Schleife prüfen, ob es am Ende geklappt hat
    //            if (result != null && result.CompilerErrors?.Any() == true)
    //            {
    //                // Hier Logik einfügen, falls der Code auch nach 3 Versuchen noch Fehler hat
    //                Console.WriteLine($"Kompilierung fehlgeschlagen nach {MaxRetries} Versuchen.");
    //            }
    //            else
    //            {
    //                Console.WriteLine("Unit Test erfolgreich repariert und kompiliert!");
    //            }






    //            return "ok .....";
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        return ex.Message;
    //    }
    //}


   // public async Task<string> ProcessProject(string projectDir, string prompt, Action<string>? logInfo = null)
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

                for (int attempt = 1; attempt <= MaxRetries; attempt++)
                {
                    // Für die Compiler-Validierung stecken wir den puren Testmethoden-Code in ein temporäres Klassengerüst
                    string validationClassStructure = PrepareValidationStructure(testClassName, targetClass.Parent as NamespaceDeclarationSyntax, testMethodCode);

                    // Code gegen den echten C#-Compiler prüfen
                    result = await manager.SetupAndValidateTestAsync(sourceFilePath, validationClassStructure);

                    // Wenn keine Fehler zurückgegeben wurden, ist die Methode valide!
                    if (result.CompilerErrors == null || !result.CompilerErrors.Any())
                    {
                        isCompiledSuccessfully = true;
                        break;
                    }

                    logInfo?.Invoke($"[NetAI] Test for '{methodName}' failed compilation (Attempt {attempt}/{MaxRetries}). Running AI repair loop...");

                    if (result.CompilerErrors.Any(i => i.Contains("Could not automatically resolve a NuGet package for the namespace(s)")))
                    {
                        logInfo?.Invoke("[NetAI] Warning: Missing NuGet dependencies detected in generated test.");
                    }

                    // Fehlertexte sammeln und Reparatur-Prompt an die KI senden
                    var errorsText = string.Join("\n", result.CompilerErrors);
                    var errorPrompt = aiPromptBuilderSimple.FixUnittestPromptSimple(errorsText, validationClassStructure);

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