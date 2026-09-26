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
            var result = await localLlmClient.AskAsync(prompt, "");


            var solutionDirectory = @"";
            var sourceFilePath = "";
            var testClassWithMethods = "";
            var _manager = new TestProjectManager(solutionDirectory);
            _manager.SetupAndValidateTestAsync(sourceFilePath, testClassWithMethods);



            //  var apiResponse = await CallTranslationApi(requestData);


            return "ok .....";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

}