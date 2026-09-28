using DotNet10TestGenerator;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models;
using System.Text.RegularExpressions;
using NetAI.TestGenerator.Core.Services;

namespace NetAI.TestGenerator.Core;

public class ResxTranslationOrchestrator
{
    private readonly HttpClient _httpClient;

    public ResxTranslationOrchestrator(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6000) };
    }

    public async Task<string> ProcessProject(string projectDir, string prompt, Action<string>? logInfo = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            {
                return "empty";
            }

            logInfo?.Invoke($"[AI-Translator] Starting analysis in directory: {projectDir}");




            var requestData = new ApiTranslationRequest
            {
                TargetLanguage = "it",
                //Items = missingTranslations.Select(entry => new ApiTranslationItem
                //{
                //    Key = entry.Key,
                //    SourceText = FindSourceTextForKey(entry.Key, currentFile, parsedResources)
                //}).ToList()
            };


            var localLlmClient = new LocalLlmClient();


            var testpath = @"C:\temp\__trash\Textdokument.cs";

            if (File.Exists(testpath + "xxx"))
            {
                //             


                var sourceFilePath =
                    @"C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Core\LocalLlmClient.cs";
                var testClassWithMethods = File.ReadAllText(testpath);

                var manager = new TestProjectManager();
                var result = await manager.SetupAndValidateTestAsync(sourceFilePath, testClassWithMethods);


                var erors = string.Join("\n", result.CompilerErrors.ToList());

                if (!string.IsNullOrEmpty(erors))
                {
                    var aiPromptBuilderSimple = new AiPromptBuilderSimple();


                    var errorPrompt = aiPromptBuilderSimple.FixUnittestPrompt(testClassWithMethods, erors);
                    var newTestClass = await localLlmClient.AskAsync(errorPrompt, "Du bist ein präziser C#-Compiler-Assistent. Deine einzige Aufgabe ist es, Syntax- und Kompilierfehler in bereitgestelltem C#-Code exakt zu reparieren und lauffähigen Code ohne Text-Erklärungen zurückzugeben.\n");
                }

                // TODO: replace testclass in prompt an ask ai agein.


                return "ok .....";
            }
            else
            {

                var sourceFilePath = @"C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Core\LocalLlmClient.cs";
                var aiPromptBuilderSimple = new AiPromptBuilderSimple();
                var manager = new TestProjectManager();

                // 1. Ersten Testentwurf generieren
                var newTestClass = await localLlmClient.AskAsync(prompt, "");
                newTestClass = newTestClass.Replace("using NSubstitute;", "");
                var testClassWithMethods = ExtractTestClass(newTestClass);

                // 2. Validierungsschleife (Maximal 3 Reparaturversuche)
                const int MaxRetries = 3;
                TestGenerationResult result = null;

                for (int attempt = 1; attempt <= MaxRetries; attempt++)
                {
                    result = await manager.SetupAndValidateTestAsync(sourceFilePath, testClassWithMethods);

                    if (result.CompilerErrors == null || !result.CompilerErrors.Any())
                    {
                        break;
                    }

                    if (result.CompilerErrors.Any(i => i.Contains("Could not automatically resolve a NuGet package for the namespace(s)")))
                    {
                        // Hier ggf. Logik einbauen oder mitsenden
                    }

                    var errorsText = string.Join("\n", result.CompilerErrors);
                    var errorPrompt = aiPromptBuilderSimple.FixUnittestPromptSimple(errorsText, testClassWithMethods);

                    var systemPrompt = "Du bist ein präziser C#-Compiler-Assistent. Deine einzige Aufgabe ist es, " +
                                       "Syntax- und Kompilierfehler in bereitgestelltem C#-Code exakt zu reparieren " +
                                       "und lauffähigen Code ohne Text-Erklärungen zurückzugeben.\n";

                    var correctedOutput = await localLlmClient.AskAsync(errorPrompt, systemPrompt);

                    testClassWithMethods = ExtractTestClass(correctedOutput);
                }

                if (result != null && result.CompilerErrors?.Any() == true)
                {
                    Console.WriteLine($"Kompilierung fehlgeschlagen nach {MaxRetries} Versuchen.");
                }
                else
                {
                    Console.WriteLine("Unit Test erfolgreich repariert und kompiliert!");
                }

                return "ok .....";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }


    public static string ExtractTestClass(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
            return string.Empty;

        var codeBlockRegex = new Regex(@"```(?:csharp|cs)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        var match = codeBlockRegex.Match(aiResponse);

        string rawCode = match.Success ? match.Groups[1].Value : aiResponse;

        int startIndex = rawCode.IndexOf("using ");
        if (startIndex == -1)
        {
            startIndex = rawCode.IndexOf("namespace ");
        }

        if (startIndex == -1)
            return rawCode.Trim(); 
        int firstOpenBrace = rawCode.IndexOf('{', startIndex);
        if (firstOpenBrace == -1)
            return rawCode.Substring(startIndex).Trim();

        int braceCount = 1;
        int endIndex = -1;

        for (int i = firstOpenBrace + 1; i < rawCode.Length; i++)
        {
            if (rawCode[i] == '{') braceCount++;
            else if (rawCode[i] == '}') braceCount--;

            if (braceCount == 0)
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex != -1)
        {
            return rawCode.Substring(startIndex, endIndex - startIndex + 1).Trim();
        }

        return rawCode.Substring(startIndex).Trim();
    }
}