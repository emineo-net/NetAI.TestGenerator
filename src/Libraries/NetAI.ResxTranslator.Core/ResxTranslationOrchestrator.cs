// Input an die KI:
//[KEY:1] Key_Name_1 ||| Source Text One
//[KEY:2] Key_Name_2 ||| Source Text Two

//Output von der KI:
//[KEY:1] Übersetzung Eins
//[KEY:2] Übersetzung Zwei




using NetAI.ResxTranslator.Core.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using NetAI.ResxTranslator.Core.Config;

namespace NetAI.ResxTranslator.Core
{
    public class ResxTranslationOrchestrator
    {
        private readonly ProjectResxAnalyzer _analyzer;
        private readonly HttpClient _httpClient;

        public ResxTranslationOrchestrator(ProjectResxAnalyzer? analyzer = null, HttpClient? httpClient = null)
        {
            _analyzer = analyzer ?? new ProjectResxAnalyzer();
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6000) };
        }

        public async Task<ResxTranslationResult> ProcessProject(
            string projectDir,
            TranslatorConfig settings,
            Action<string>? logInfo = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                {
                    return ResxTranslationResult.Fail($"The project directory does not exist or is invalid: '{projectDir}'");
                }

                logInfo?.Invoke($"[AI-Translator] Starting analysis in directory: {projectDir}");

                List<string> resxFiles = _analyzer.FindResxFiles(projectDir);
                if (resxFiles.Count == 0)
                {
                    logInfo?.Invoke("[AI-Translator] No .resx files found in the project directory.");
                    return ResxTranslationResult.Ok();
                }

                List<ResxFileInfo> parsedResources = _analyzer.ReadResxFiles(resxFiles, logInfo);
                if (parsedResources.Count == 0)
                {
                    return ResxTranslationResult.Fail(".resx files were found, but none could be successfully read.");
                }

                string projectDefaultLang = TranslatorLanguageResolver.ResolveDefault(settings.DefaultLanguage, logInfo);
                logInfo?.Invoke($"[AI-Translator] Default language: '{projectDefaultLang}'");

                List<string> supportedLanguages = TranslatorLanguageResolver.ResolveSupported(settings.SupportedLanguages, logInfo);

                if (supportedLanguages.Count > 0)
                {
                    logInfo?.Invoke($"[AI-Translator] Configured target languages: {string.Join(", ", supportedLanguages)}");

                    List<ResxFileInfo> newlyCreatedFiles = _analyzer.EnsureSupportedLanguageFiles(
                        parsedResources, projectDefaultLang, supportedLanguages, logInfo);

                    if (newlyCreatedFiles.Count > 0)
                    {
                        parsedResources.AddRange(newlyCreatedFiles);
                    }
                }

                foreach (var currentFile in parsedResources)
                {
                    try
                    {
                        string fileLang = _analyzer.GetLanguageFromFileName(currentFile.FilePath);

                        if (fileLang == "neutral")
                        {
                            fileLang = projectDefaultLang;
                        }

                        var missingTranslations = currentFile.Entries.Where(e => !e.HasTranslation).ToList();

                        // ==========================================
                        // LOGIK IN DIE API VERSCHOBEN AB HIER
                        // ==========================================
                        if (missingTranslations.Any())
                        {
                            logInfo?.Invoke($"[AI-Translator] File '{currentFile.FileName}' recognized as language '{fileLang}'. {missingTranslations.Count} gaps found. Requesting API translation...");

                            // 1. DTO-Payload aufbauen
                            var requestData = new ApiTranslationRequest
                            {
                                TargetLanguage = fileLang,
                                Items = missingTranslations.Select(entry => new ApiTranslationItem
                                {
                                    Key = entry.Key,
                                    SourceText = FindSourceTextForKey(entry.Key, currentFile, parsedResources)
                                }).ToList()
                            };

                            // 2. Synchroner API-Aufruf (Nutzt Newtonsoft, da .NET Standard 2.0)
                            ApiTranslationResponse apiResponse = await CallTranslationApi(requestData);

                            if (!apiResponse.IsSuccess)
                            {
                                logInfo?.Invoke($"[AI-Translator Error] API translation failed: {apiResponse.ErrorMessage}");
                                continue;
                            }

                            // 3. Antworten wieder in die ResxEntries mappen
                            foreach (var entry in missingTranslations)
                            {
                                if (apiResponse.Translations.TryGetValue(entry.Key, out string? translatedText))
                                {
                                    entry.Value = translatedText;
                                }
                                else
                                {
                                    logInfo?.Invoke($"[AI-Translator Warning] Missing translation from API for key '{entry.Key}'");
                                    entry.Value = $"[Translation missing for '{entry.Key}']";
                                }
                            }

                            // 4. Direkt wegschreiben
                            _analyzer.SaveTranslations(currentFile.FilePath, missingTranslations, logInfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        logInfo?.Invoke($"[AI-Translator Error] Error processing file '{currentFile.FilePath}': {ex.Message}");
                    }
                }

                return ResxTranslationResult.Ok();
            }
            catch (Exception ex)
            {
                return ResxTranslationResult.Fail($"Critical error while executing the AI translator: {ex.Message}");
            }
        }

        private async Task<ApiTranslationResponse> CallTranslationApi(ApiTranslationRequest request)
        {
            var localLlmClient = new LocalLlmClient();
            var builder = new TranslationPromptBuilder();

            // --- SCHRITT 1: DOMÄNEN-ANALYSE ---
            var (analysisPrompt, analysisInput) = builder.BuildDomainAnalysisPrompt(request);

            // Aufruf: userMessage zuerst, systemMessage als zweiter Parameter
            string rawDomain = await localLlmClient.AskAsync(analysisInput, analysisPrompt);

            // Sicherheitsgurt: Bereinige den Output des lokalen LLMs von "Geister-Präfixen"
            string detectedDomain = rawDomain
                .Replace("System:", "")
                .Replace("User:", "")
                .Replace("Assistant:", "")
                .Replace("**", "") // Verhindert Formatierungs-Artefakte
                .Trim('\r', '\n', ' ', '"', '.');

            // Fallback falls die Analyse leer oder fehlerhaft war
            if (string.IsNullOrWhiteSpace(detectedDomain) || detectedDomain.Length > 50)
            {
                detectedDomain = "General Software UI";
            }

            // --- SCHRITT 2: ÜBERSETZUNGS-BATCHES ---
            var batches = builder.BuildBatchPrompts(request, detectedDomain);
            var translatedDictionary = new Dictionary<string, string>();

            // KORREKTUR: [^\]]+ statt [^\]\s]+ erlaubt Leerzeichen innerhalb des Keys
            var keyRegex = new Regex(@"\[\s*KEY\s*:\s*(?<key>[^\]]+)\s*\]\s*(?<translation>.*)", RegexOptions.Compiled);


            foreach (var batch in batches)
            {
                var answer = await localLlmClient.AskAsync(batch.UserStructuredInput, batch.SystemPrompt);

                // Zeilenweise verarbeiten
                var lines = answer.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var match = keyRegex.Match(line);
                    if (match.Success)
                    {
                        string key = match.Groups["key"].Value.Trim();
                        string translation = match.Groups["translation"].Value.Trim();

                        // Sicherheits-Check: Nur speichern, wenn der extrahierte Key auch im aktuellen Batch angefordert wurde
                        if (batch.UserStructuredInput.Contains($"[KEY:{key}]"))
                        {
                            // Falls das Modell die Übersetzung komplett vergessen hat (leerer String), 
                            // behalten wir lieber den Originaltext oder vermerken es
                            translatedDictionary[key] = string.IsNullOrWhiteSpace(translation)
                                ? "[Translation Missing]"
                                : translation;
                        }
                    }
                    else
                    {
                        // Optional: Hier könntest du Protokollieren, wenn das Modell eine Zeile komplett zerstört hat
                        // logInfo?.Invoke($"[AI-Translator Warning] Could not parse line: {line}");
                    }
                }
            }

            // --- SCHRITT 3: RESPONSE AN ORCHESTRATOR ZURÜCKGEBEN ---
            return new ApiTranslationResponse
            {
                IsSuccess = true,
                Translations = translatedDictionary
            };
        }


        private string FindSourceTextForKey(string key, ResxFileInfo currentFile, List<ResxFileInfo> allFiles)
        {
            try
            {
                string baseFileName = _analyzer.GetBaseResourceName(currentFile.FileName);
                var mainFile = allFiles.FirstOrDefault(f => f.FileName.Equals($"{baseFileName}.resx", StringComparison.OrdinalIgnoreCase));

                var mainEntry = mainFile?.Entries.FirstOrDefault(e => e.Key == key);
                if (mainEntry != null && !string.IsNullOrWhiteSpace(mainEntry.Value))
                {
                    return mainEntry.Value;
                }

                foreach (var file in allFiles)
                {
                    var siblingEntry = file.Entries.FirstOrDefault(e => e.Key == key);
                    if (siblingEntry != null && !string.IsNullOrWhiteSpace(siblingEntry.Value))
                    {
                        return siblingEntry.Value;
                    }
                }
            }
            catch
            {
                // fallback
            }

            return key;
        }
    }
}