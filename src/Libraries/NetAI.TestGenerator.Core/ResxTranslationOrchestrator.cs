using DotNet10TestGenerator;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models;
using System.Text.RegularExpressions;

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

            if (File.Exists(testpath))
            {
//             


                var sourceFilePath =
                    @"C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Core\LocalLlmClient.cs";
                var testClassWithMethods = File.ReadAllText(testpath);


                var manager = new TestProjectManager();
                var result = await manager.SetupAndValidateTestAsync(sourceFilePath, testClassWithMethods);


                //  var apiResponse = await CallTranslationApi(requestData);

                return "ok .....";
            }
            else
            {





                var newTestClass = await localLlmClient.AskAsync(prompt, "");


                var sourceFilePath =
                    @"C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Core\LocalLlmClient.cs";
                var testClassWithMethods = ExtractTestClass(newTestClass);


                var manager = new TestProjectManager();
                var result = await manager.SetupAndValidateTestAsync(sourceFilePath, testClassWithMethods);


                //  var apiResponse = await CallTranslationApi(requestData);

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

            // 1. Schritt: Extrahiere den Inhalt aus Markdown-Code-Blöcken, falls vorhanden
            var codeBlockRegex = new Regex(@"```(?:csharp|cs)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
            var match = codeBlockRegex.Match(aiResponse);

            string rawCode = match.Success ? match.Groups[1].Value : aiResponse;

            // 2. Schritt: Finde den echten Start des C#-Codes (entweder 'using ' oder 'namespace ')
            int startIndex = rawCode.IndexOf("using ");
            if (startIndex == -1)
            {
                startIndex = rawCode.IndexOf("namespace ");
            }

            if (startIndex == -1)
                return rawCode.Trim(); // Fallback, falls weder using noch namespace existiert

            // 3. Schritt: Finde das exakte Ende der Klasse über die geschweiften Klammern
            int firstOpenBrace = rawCode.IndexOf('{', startIndex);
            if (firstOpenBrace == -1)
                return rawCode.Substring(startIndex).Trim();

            int braceCount = 1;
            int endIndex = -1;

            // Wir laufen ab der ersten offenen Klammer durch den Code und zählen mit
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

            // Wenn wir das korrekte Ende gefunden haben, schneiden wir exakt diesen Teil aus
            if (endIndex != -1)
            {
                return rawCode.Substring(startIndex, endIndex - startIndex + 1).Trim();
            }

            return rawCode.Substring(startIndex).Trim();
        }



    }