using System.Collections;
using System.Runtime.CompilerServices;

namespace NetAI.TestGenerator.Core.Services;

public static class BuildLogger
{
    private static DateTime _lastLogTime = DateTime.Now;
    private static readonly bool LogEnabel = true;

    public static readonly string LogFilePath = Path.Combine(
        @"C:\Closerpage\__BuildLogTestGenerator", $"BuildLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

    public static void Info<T>(T value, [CallerArgumentExpression(nameof(value))] string varName = "Unknown",
        [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
    {
        Log("INFO", value, varName, lineNumber, memberName, filePath);
    }

    public static void Warning<T>(T value, [CallerArgumentExpression(nameof(value))] string varName = "Unknown",
        [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
    {
        Log("WARN", value, varName, lineNumber, memberName, filePath);
    }

    public static void Error<T>(T value, [CallerArgumentExpression(nameof(value))] string varName = "Unknown",
        [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
    {
        Log("ERROR", value, varName, lineNumber, memberName, filePath);
    }

    private static void Log<T>(string level, T value, string varName, int lineNumber, string memberName, string filePath)
    {
        try
        {
            if (!LogEnabel)
            {
                return;
            }

            var now = DateTime.Now;
            var secondsSinceLastLog = (now - _lastLogTime).TotalSeconds;
            _lastLogTime = now;

            var timeDelta = $"+{secondsSinceLastLog:F2}s";
            var formattedValue = FormatValue(value);

            // Caller-Informationen zuerst
            var callerInfo = $" ├─ Variable : {varName}{Environment.NewLine}" + $" ├─ Member   : {memberName}{Environment.NewLine}" +
                             $" ├─ File     : {filePath}{Environment.NewLine}" + $" ├─ Line     : {lineNumber}";

            // Das Log-Level (INFO, WARN, ERROR) wird links mit ausgegeben
            var logLine = $"[{now:HH:mm:ss} | {level} | {timeDelta}]{Environment.NewLine}" + callerInfo + Environment.NewLine +
                          $" └─ Value    : {formattedValue}{Environment.NewLine}" + new string('-', 80) + Environment.NewLine;

            File.AppendAllText(LogFilePath, logLine);

            if (formattedValue == "DONE")
            {
                using var _ = File.OpenRead(LogFilePath);
            }
        }
        catch (Exception)
        {
            // Verhindert Build-Absturz bei Fehlern im Logging
        }
    }

    private static string FormatValue<T>(T value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string s)
        {
            return s;
        }

        if (value is IDictionary dict)
        {
            var entries = dict.Cast<DictionaryEntry>().Select(e => $"[{e.Key}] = {e.Value}");
            return "{" + string.Join(", ", entries) + "}";
        }

        if (value is IEnumerable enumerable)
        {
            var items = enumerable.Cast<object>().Select(o => o?.ToString() ?? "null");
            return "[" + string.Join(", ", items) + "]";
        }

        return value.ToString() ?? "null";
    }
}