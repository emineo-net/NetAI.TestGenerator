namespace NetAI.TestGenerator.Core.Models;

/// <summary>Contains the prompts prepared for one translation batch.</summary>
public class TranslationBatchPromptResult
{

    /// <summary>Gets or sets the system instructions for the translation model.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Gets or sets the keyed resource strings supplied as user input.</summary>
    public string UserStructuredInput { get; set; } = string.Empty;
}