using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetAI.ResxTranslator.Core;

/// <summary>
/// Validiert und normalisiert die Sprach-Einstellungen aus aisettings.json.
/// Ersetzt ProjectResxAnalyzer.DetermineDefaultLanguage / DetermineSupportedLanguages,
/// die bisher die .csproj gelesen haben.
/// </summary>
public static class TranslatorLanguageResolver
{
    public const string FallbackLanguage = "en";

    public static string ResolveDefault(string? configured, Action<string>? logInfo = null)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            logInfo?.Invoke($"[AI-Translator] No 'translator.defaultLanguage' in aisettings.json - using '{FallbackLanguage}'.");
            return FallbackLanguage;
        }

        string value = configured!.Trim();
        if (IsValidCultureCode(value))
        {
            return value;
        }

        logInfo?.Invoke($"[AI-Translator Warning] Invalid 'translator.defaultLanguage' '{value}' in aisettings.json - using '{FallbackLanguage}'.");
        return FallbackLanguage;
    }

    public static List<string> ResolveSupported(IEnumerable<string>? configured, Action<string>? logInfo = null)
    {
        var result = new List<string>();

        if (configured != null)
        {
            foreach (var entry in configured)
            {
                // Toleranz: "de, fr" in einem einzelnen Eintrag wird ebenfalls akzeptiert
                foreach (var part in (entry ?? string.Empty).Split(','))
                {
                    string code = part.Trim();
                    if (code.Length == 0) continue;

                    if (!IsValidCultureCode(code))
                    {
                        logInfo?.Invoke($"[AI-Translator Warning] Invalid language code '{code}' in 'translator.supportedLanguages' ignored.");
                        continue;
                    }

                    if (!result.Contains(code, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(code);
                    }
                }
            }
        }

        if (result.Count == 0)
        {
            logInfo?.Invoke("[AI-Translator Warning] No valid languages in 'translator.supportedLanguages' in aisettings.json - only existing .resx files will be completed.");
        }

        return result;
    }

    public static bool IsValidCultureCode(string? cultureCode)
    {
        if (string.IsNullOrWhiteSpace(cultureCode)) return false;
        try
        {
            _ = CultureInfo.GetCultureInfo(cultureCode!);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}