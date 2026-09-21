using System.Collections.Generic;

namespace NetAI.ResxTranslator.Core.Config;

/// <summary>
/// Abbild des "translator"-Blocks in aisettings.json.
/// Ersetzt sämtliche früheren .csproj-Properties
/// (AiTranslatorApiKey, AiTranslatorContext, AiTranslatorGlossaryPath,
///  AiTranslatorMode, NeutralLanguage, SupportedLanguage).
/// Die Property-Namen werden von Newtonsoft standardmäßig case-insensitiv gemappt
/// ("supportedLanguages" -> SupportedLanguages).
/// </summary>
public class TranslatorConfig
{
    public string Mode { get; set; } = "all";
    public string ApiKey { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string GlossaryPath { get; set; } = string.Empty;
    public string DefaultLanguage { get; set; } = "en";
    public List<string> SupportedLanguages { get; set; } = new();
}