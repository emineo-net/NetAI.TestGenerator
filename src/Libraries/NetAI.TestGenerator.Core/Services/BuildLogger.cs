using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NetAI.TestGenerator.Core.Services;

#if MY_LOCAL_LOGGING
/// <summary>Writes detailed build messages to a local log file.</summary>
public static class BuildLogger
{
    private static DateTime _lastLogTime = DateTime.Now;
    private static readonly bool LogEnabel = true;

    /// <summary>Gets the path of the local build log.</summary>
    public static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetAI",
        "TestGenerator",
        "BuildLogs",
        $"BuildLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

    /// <summary>Logs an informational message with caller details.</summary>
    public static void Info<T>(T value, [CallerArgumentExpression(nameof(value))] string varName = "Unknown",
        [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
    {
        Log("INFO", value, varName, lineNumber, memberName, filePath);
    }

    /// <summary>Logs a warning with caller details.</summary>
    public static void Warning<T>(T value, [CallerArgumentExpression(nameof(value))] string varName = "Unknown",
        [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
    {
        Log("WARN", value, varName, lineNumber, memberName, filePath);
    }

    /// <summary>Logs an error with caller details.</summary>
    public static void Error<T>(T value, [CallerArgumentExpression(nameof(value))] string varName = "Unknown",
        [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
    {
        Log("ERROR", value, varName, lineNumber, memberName, filePath);
    }

    private static void Log<T>(string level, T value, string varName, int lineNumber, string memberName, string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!LogEnabel)
                return;

            var now = DateTime.Now;
            var secondsSinceLastLog = (now - _lastLogTime).TotalSeconds;
            _lastLogTime = now;
            var timeDelta = $"+{secondsSinceLastLog:F2}s";
            var formattedValue = FormatValue(value);
            var callerInfo = $" ├─ Variable : {varName}{Environment.NewLine}" +
                             $" ├─ Member   : {memberName}{Environment.NewLine}" +
                             $" ├─ File     : {filePath}{Environment.NewLine}" +
                             $" ├─ Line     : {lineNumber}";
            var logLine = $"[{now:HH:mm:ss} | {level} | {timeDelta}]{Environment.NewLine}" + callerInfo +
                          Environment.NewLine + $" └─ Value    : {formattedValue}{Environment.NewLine}" +
                          new string('-', 80) + Environment.NewLine;
            File.AppendAllText(LogFilePath, logLine);

            if (formattedValue == "DONE")
            {
                using var _ = File.OpenRead(LogFilePath);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string FormatValue<T>(T value)
    {
        if (value is null)
            return "null";
        if (value is string text)
            return text;
        if (value is IDictionary dictionary)
        {
            var entries = dictionary.Cast<DictionaryEntry>().Select(entry => $"[{entry.Key}] = {entry.Value}");
            return "{" + string.Join(", ", entries) + "}";
        }
        if (value is IEnumerable enumerable)
        {
            var items = enumerable.Cast<object>().Select(item => item?.ToString() ?? "null");
            return "[" + string.Join(", ", items) + "]";
        }

        return value.ToString() ?? "null";
    }
}
#endif
#if !MY_LOCAL_LOGGING
/// <summary>Writes build messages at the appropriate log level.</summary>
public static class BuildLogger
{

    /// <summary>Logs an informational message.</summary>
    public static void Info<T>(T value, string varName = "Unknown", int lineNumber = 0, string memberName = "", string filePath = "") { }

    /// <summary>Logs a warning message.</summary>
    public static void Warning<T>(T value, string varName = "Unknown", int lineNumber = 0, string memberName = "", string filePath = "") { }

    /// <summary>Logs an error message.</summary>
    public static void Error<T>(T value, string varName = "Unknown", int lineNumber = 0, string memberName = "", string filePath = "") { }
}
#endif
