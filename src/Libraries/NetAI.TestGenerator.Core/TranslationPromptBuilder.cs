using System.Text;
using NetAI.TestGenerator.Core.Models;

namespace NetAI.TestGenerator.Core;

/// <summary>Builds domain-analysis and structured batch prompts for resource-file localization.</summary>
public class TranslationPromptBuilder
{

    private const int BatchSize = 20;

    /// <summary>Creates a prompt that asks the model to identify the domain of a translation request.</summary>
    public (string SystemPrompt, string AnalysisInput) BuildDomainAnalysisPrompt(ApiTranslationRequest request)
    {
        var sampleTexts = request.Items.Take(15).Select(i => i.SourceText);
        var analysisInput = string.Join("\n", sampleTexts);

        var analysisSystemPrompt = """
                                   You are a linguistic analyzer. Analyze the following software localization strings and determine the primary industry, topic, or domain (e.g., Medical, Finance, Automotive, Gaming, E-Commerce, General IT).
                                   Respond with ONLY the name of the domain in 1-3 words. Do NOT write full sentences or explanations.
                                   """;

        return (analysisSystemPrompt, analysisInput);
    }

    /// <summary>Builds structured translation prompts in batches of up to 20 resource strings.</summary>
    public List<TranslationBatchPromptResult> BuildBatchPrompts(ApiTranslationRequest request, string detectedDomain)
    {
        var results = new List<TranslationBatchPromptResult>();

        detectedDomain = detectedDomain?.Trim('\r', '\n', ' ', '"', '.') ?? "";
        if (string.IsNullOrWhiteSpace(detectedDomain) || detectedDomain.Length > 50)
        {
            detectedDomain = "General Software UI";
        }

        var domainContextPrompt = $$"""
                                    ### CONTEXT / DOMAIN KNOWLEDGE:
                                    - The software being translated belongs to the domain: **{{detectedDomain}}**.
                                    - Use terminology, vocabulary, and jargon appropriate for this specific field.
                                    """;

        for (var i = 0; i < request.Items.Count; i += BatchSize)
        {
            var batchEnd = Math.Min(i + BatchSize, request.Items.Count);

            var sbPrompt = new StringBuilder();
            for (var itemIndex = i; itemIndex < batchEnd; itemIndex++)
            {
                var item = request.Items[itemIndex];
                sbPrompt.AppendLine($"[KEY:{item.Key}] ||| {item.SourceText}");
            }

            var structuredInput = sbPrompt.ToString();
            var isKeySentenceStyle = HasSentenceStyleKey(structuredInput);

            var systemPromptStandard = $$"""
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

            var systemPromptAlternativ = $$"""
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
                                           [KEY:lbl_assigned_categories] assegna categorie alle tue pagine o eliminale
                                           [KEY:msg_welcome] Bentornato, {0}!
                                           """;

            var finalSystemPrompt = isKeySentenceStyle ? systemPromptAlternativ : systemPromptStandard;

            results.Add(new TranslationBatchPromptResult { SystemPrompt = finalSystemPrompt, UserStructuredInput = structuredInput });
        }

        return results;
    }

    private static bool HasSentenceStyleKey(string structuredInput)
    {
        var lineStart = 0;
        while (lineStart < structuredInput.Length)
        {
            var lineEnd = structuredInput.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = structuredInput.Length;
            }

            if (lineEnd - lineStart >= 5 && string.CompareOrdinal(structuredInput, lineStart, "[KEY:", 0, 5) == 0)
            {
                var keyStart = lineStart + 5;
                var closingBracket = structuredInput.IndexOf(']', keyStart);
                if (closingBracket > keyStart && closingBracket < lineEnd &&
                    structuredInput.IndexOf(' ', keyStart, closingBracket - keyStart) >= 0)
                {
                    return true;
                }
            }

            if (lineEnd == structuredInput.Length)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return false;
    }
}