using System;
using System.Collections.Generic;
using System.Text;

namespace NetAI.ResxTranslator.Core.Models
{
    /// <summary>
    /// Ergebnis eines Übersetzungs-Durchlaufs. Kapselt Erfolg/Fehler,
    /// damit der aufrufende MSBuild-Task nur noch das Ergebnis loggen muss.
    /// </summary>
    public class ResxTranslationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }

        public static ResxTranslationResult Ok() => new() { Success = true };

        public static ResxTranslationResult Fail(string errorMessage) =>
            new() { Success = false, ErrorMessage = errorMessage };
    }
}
