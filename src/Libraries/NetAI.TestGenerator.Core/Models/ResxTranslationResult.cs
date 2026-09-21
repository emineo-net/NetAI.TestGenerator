namespace NetAI.TestGenerator.Core.Models;

/// <summary>
///     Ergebnis eines Übersetzungs-Durchlaufs. Kapselt Erfolg/Fehler,
///     damit der aufrufende MSBuild-Task nur noch das Ergebnis loggen muss.
/// </summary>
public class ResxTranslationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public static ResxTranslationResult Ok()
    {
        return new ResxTranslationResult { Success = true };
    }

    public static ResxTranslationResult Fail(string errorMessage)
    {
        return new ResxTranslationResult { Success = false, ErrorMessage = errorMessage };
    }
}