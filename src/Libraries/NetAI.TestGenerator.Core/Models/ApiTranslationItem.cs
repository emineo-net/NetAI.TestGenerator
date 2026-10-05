namespace NetAI.TestGenerator.Core.Models;

/// <summary>Represents one keyed resource string submitted for translation.</summary>
public class ApiTranslationItem
{

    /// <summary>Gets or sets the stable resource key that identifies this string.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the source-language text associated with the key.</summary>
    public string SourceText { get; set; } = string.Empty;
}