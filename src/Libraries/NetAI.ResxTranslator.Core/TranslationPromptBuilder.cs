using NetAI.ResxTranslator.Core.Models;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetAI.ResxTranslator.Core;

public class TranslationPromptBuilder
{
    private const int BatchSize = 20;

    /// <summary>
    /// Generiert den Input und den System-Prompt für die einmalige Domänen-Analyse.
    /// </summary>
    public (string SystemPrompt, string AnalysisInput) BuildDomainAnalysisPrompt(ApiTranslationRequest request)
    {
        var sampleTexts = request.Items.Take(15).Select(i => i.SourceText);
        string analysisInput = string.Join("\n", sampleTexts);

        string analysisSystemPrompt = """
            You are a linguistic analyzer. Analyze the following software localization strings and determine the primary industry, topic, or domain (e.g., Medical, Finance, Automotive, Gaming, E-Commerce, General IT).
            Respond with ONLY the name of the domain in 1-3 words. Do NOT write full sentences or explanations.
            """;

        return (analysisSystemPrompt, analysisInput);
    }

    /// <summary>
    /// Teilt die Items in Batches auf und generiert für jeden Batch den System-Prompt und den strukturierten User-Input.
    /// </summary>
    public List<TranslationBatchPromptResult> BuildBatchPrompts(ApiTranslationRequest request, string detectedDomain)
    {
        var results = new List<TranslationBatchPromptResult>();

        // Domänen-Kontext bereinigen und vorbereiten
        detectedDomain = detectedDomain?.Trim('\r', '\n', ' ', '"', '.') ?? "";
        if (string.IsNullOrWhiteSpace(detectedDomain) || detectedDomain.Length > 50)
        {
            detectedDomain = "General Software UI";
        }

        string domainContextPrompt = $$"""
            ### CONTEXT / DOMAIN KNOWLEDGE:
            - The software being translated belongs to the domain: **{{detectedDomain}}**.
            - Use terminology, vocabulary, and jargon appropriate for this specific field.
            """;

        // Verarbeitung in Batches
        for (int i = 0; i < request.Items.Count; i += BatchSize)
        {
            var batch = request.Items.Skip(i).Take(BatchSize).ToList();

            // 1. Strukturierten Input (User Message) generieren
            var sbPrompt = new StringBuilder();
            foreach (var item in batch)
            {
                sbPrompt.AppendLine($"[KEY:{item.Key}] ||| {item.SourceText}");
            }
            string structuredInput = sbPrompt.ToString();

            // 2. Entscheidung über Prompt-Variante (Regex-Logik)
            bool isKeySentenceStyle = false;
            var keyMatches = Regex.Matches(structuredInput, @"^\[KEY:(?<key>.*?)\]", RegexOptions.Multiline);
            foreach (Match match in keyMatches)
            {
                string currentKey = match.Groups["key"].Value;
                if (currentKey.Contains(" "))
                {
                    isKeySentenceStyle = true;
                    break;
                }
            }

            // 3. Prompt-Templates befüllen
            string systemPromptStandard = $$"""
                You are a professional translation assistant specializing in software localization (.resx files).
                Your sole task is to translate the provided text into the target language: "{{request.TargetLanguage}}".

                {{domainContextPrompt}}

                ### FORMATTING RULES (STRICT COMPLIANCE REQUIRED):
                1. Respond EXCLUSIVELY in the requested structured text format.
                2. Every single output line MUST start with the exact prefix: `[KEY:identifier] ` followed immediately by the translation.
                3. NEVER alter, omit, or add spaces inside or around the `[KEY:...]` brackets. It must match the input key character-for-character.
                4. Do NOT add a space between '[KEY:' and the identifier.
                5. Do NOT include any markdown code blocks (like ```text or ```), introductions, or explanations. Output ONLY the raw lines.
                
                ### TRANSLATION GUIDELINES:
                - Preserve all technical placeholders exactly as they are (e.g., {0}, {name}, %s, \n, \t). Do not translate, alter, or reorder them unless grammatically required. Always translate the source text that appears AFTER the ||| separator.
                - Translate text in the context of software user interfaces (UI elements, buttons, labels, error messages). Choose concise and natural terminology for the target language.
                - If a source text is empty or contains only placeholders, return the key with the placeholders unchanged.

                ### EXAMPLE INPUT AND OUTPUT:
                Input (User):
                [KEY:Buttons.Cancel] ||| Cancel
                [KEY:Messages.WelcomeUser] ||| Welcome back, {0}!

                Output (You):
                [KEY:Buttons.Cancel] Abbrechen
                [KEY:Messages.WelcomeUser] Willkommen zurück, {0}!
                """;

            string systemPromptAlternativ = $$"""
                You are a professional translation assistant specializing in software localization (.resx files).
                Your sole task is to translate the provided text into the target language: "{{request.TargetLanguage}}".

                {{domainContextPrompt}}

                ### FORMATTING RULES (STRICT COMPLIANCE REQUIRED):
                1. Respond EXCLUSIVELY in the requested structured text format.
                2. Every single output line MUST start with the exact prefix: `[KEY:identifier] ` followed immediately by the translation.
                3. NEVER alter, omit, or add spaces inside or around the `[KEY:...]` brackets. It must match the input key character-for-character.
                4. Do NOT add a space between '[KEY:' and the identifier.
                5. Do NOT include any markdown code blocks (like ```text or ```), introductions, or explanations. Output ONLY the raw lines.
                
                ### TRANSLATION GUIDELINES:
                - Translate the source text that appears AFTER the `|||` separator into "{{request.TargetLanguage}}".
                - Preserve all technical placeholders exactly as they are (e.g., {0}, {name}, %s, \n, \t).
                - Translate in the context of software user interfaces. Choose concise, natural, and professional terminology.

                ### EXAMPLE INPUT AND OUTPUT:
                Input (User):
                [KEY:lbl_assigned_categories] ||| assign categories to your pages or delete them
                [KEY:msg_welcome] ||| Welcome back, {0}!

                Output (You):
                [KEY:lbl_assigned_categories] assegna le categorie deine pagine o eliminale
                [KEY:msg_welcome] Bentornato, {0}!                                        
                """;

            string finalSystemPrompt = isKeySentenceStyle ? systemPromptAlternativ : systemPromptStandard;

            results.Add(new TranslationBatchPromptResult
            {
                SystemPrompt = finalSystemPrompt,
                UserStructuredInput = structuredInput
            });
        }

        return results;
    }
}
