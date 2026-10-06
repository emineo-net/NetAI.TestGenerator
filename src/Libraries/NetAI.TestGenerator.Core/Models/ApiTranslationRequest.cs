namespace NetAI.TestGenerator.Core.Models;

/// <summary>Describes a batch translation request for keyed resource strings.</summary>
public class ApiTranslationRequest
{
    /// <summary>Gets or sets the requested target language.</summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>Gets or sets the resource strings to translate.</summary>
    public List<ApiTranslationItem> Items { get; set; } = new();
}