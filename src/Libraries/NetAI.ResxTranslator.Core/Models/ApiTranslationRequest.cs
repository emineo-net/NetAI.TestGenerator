namespace NetAI.ResxTranslator.Core.Models;

public class ApiTranslationRequest
{
    public string TargetLanguage { get; set; } = string.Empty;
    public List<ApiTranslationItem> Items { get; set; } = new();
}