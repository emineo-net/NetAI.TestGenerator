namespace NetAI.TestGenerator.Core.Models;

public class ResxEntry
{
    private bool? _hasTranslation;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;

    public bool HasTranslation
    {
        // Nutzt den manuell gesetzten Wert, falls vorhanden; andernfalls wird gerechnet
        get => _hasTranslation ?? !string.IsNullOrWhiteSpace(Value);
        set => _hasTranslation = value;
    }
}