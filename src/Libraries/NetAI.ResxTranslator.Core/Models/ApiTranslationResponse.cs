namespace NetAI.ResxTranslator.Core.Models;

public class ApiTranslationResponse
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    // Key -> Übersetzter Text
    public Dictionary<string, string> Translations { get; set; } = new();
}
