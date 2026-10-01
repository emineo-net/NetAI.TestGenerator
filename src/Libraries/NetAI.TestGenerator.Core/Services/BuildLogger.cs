


using System;
using System.IO;

namespace NetAI.TestGenerator.Core.Services;

public static class BuildLogger
{
    // Hält den Zeitpunkt des letzten Log-Eintrags fest
    private static DateTime _lastLogTime = DateTime.Now;

    public static readonly string LogFilePath = Path.Combine(
        @"C:\Closerpage\__BuildLogTestGenerator",
        $"BuildLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
    );

    public static void BuildLog(string message)
    {
        try
        {
            DateTime now = DateTime.Now;

            // Berechnet die Differenz in Sekunden (als Fließkommazahl für Millisekunden-Präzision)
            double secondsSinceLastLog = (now - _lastLogTime).TotalSeconds;

            // Aktualisiert den Zeitstempel für den nächsten Aufruf
            _lastLogTime = now;

            // Formatierung: "+0.42s" (F2 rundet auf 2 Nachkommastellen)
            string timeDelta = $"+{secondsSinceLastLog:F2}s";

            // Die finale Logzeile mit Zeitstempel und Differenz
            string logLine = $"[{now:HH:mm:ss} | {timeDelta}] {message}{Environment.NewLine}";

            File.AppendAllText(LogFilePath, logLine);

            if (logLine == "DONE")
            {
                File.OpenRead(LogFilePath);
            }
        }
        catch (Exception)
        {
            // Verhindert Build-Absturz bei Fehlern im Logging
        }
    }
}
