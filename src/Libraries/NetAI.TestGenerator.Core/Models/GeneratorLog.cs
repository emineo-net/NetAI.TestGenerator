namespace NetAI.TestGenerator.Core.Models;

/// <summary>Severity of a message emitted by the test generator.</summary>
public enum GeneratorLogLevel
{
    /// <summary>Detailed diagnostic output.</summary>
    Debug,

    /// <summary>Normal progress information.</summary>
    Info,

    /// <summary>Something went wrong for a single item, but processing continues.</summary>
    Warning,

    /// <summary>An operation failed.</summary>
    Error
}

/// <summary>Receives log messages from the test generator.</summary>
/// <param name="level">The message severity.</param>
/// <param name="message">The message text.</param>
public delegate void GeneratorLogger(GeneratorLogLevel level, string message);

/// <summary>Null-safe convenience wrappers around <see cref="GeneratorLogger"/>.</summary>
public static class GeneratorLoggerExtensions
{
    /// <summary>Logs an informational message.</summary>
    public static void Info(this GeneratorLogger? logger, string message) => logger?.Invoke(GeneratorLogLevel.Info, message);

    /// <summary>Logs a debug message.</summary>
    public static void Debug(this GeneratorLogger? logger, string message) => logger?.Invoke(GeneratorLogLevel.Debug, message);

    /// <summary>Logs a warning.</summary>
    public static void Warning(this GeneratorLogger? logger, string message) => logger?.Invoke(GeneratorLogLevel.Warning, message);

    /// <summary>Logs an error.</summary>
    public static void Error(this GeneratorLogger? logger, string message) => logger?.Invoke(GeneratorLogLevel.Error, message);

    /// <summary>Adapts the logger to a plain <see cref="Action{T}"/> that logs at <see cref="GeneratorLogLevel.Info"/>.</summary>
    public static Action<string>? AsInfoAction(this GeneratorLogger? logger) =>
        logger is null ? null : message => logger(GeneratorLogLevel.Info, message);
}
