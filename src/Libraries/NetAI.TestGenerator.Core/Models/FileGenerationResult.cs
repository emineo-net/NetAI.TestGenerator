namespace NetAI.TestGenerator.Core.Models;

/// <summary>Overall outcome of processing one source file.</summary>
public enum FileGenerationStatus
{
    /// <summary>All uncovered methods received a test or a skip stub (or nothing needed to be done).</summary>
    Success,

    /// <summary>Some methods received tests, others failed.</summary>
    PartialSuccess,

    /// <summary>No test could be generated for any of the uncovered methods, or the file could not be processed.</summary>
    Failed,

    /// <summary>The file contains nothing to test (e.g. no eligible class).</summary>
    NothingToDo
}

/// <summary>Describes the result of generating tests for one source file.</summary>
public sealed record FileGenerationResult
{
    /// <summary>Gets the overall status.</summary>
    public FileGenerationStatus Status { get; init; }

    /// <summary>Gets a human-readable summary or the error message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the number of methods for which a compilable test was generated (AI or template).</summary>
    public int Generated { get; init; }

    /// <summary>Gets the number of methods that received a skip stub instead of a real test.</summary>
    public int SkipStubs { get; init; }

    /// <summary>Gets the number of methods that were already covered by an existing test.</summary>
    public int AlreadyCovered { get; init; }

    /// <summary>Gets the number of methods for which no test could be generated.</summary>
    public int Failed { get; init; }

    /// <summary>Gets the test files that were written.</summary>
    public IReadOnlyList<string> WrittenFiles { get; init; } = Array.Empty<string>();

    /// <summary>Gets whether the run counts as successful (no failures).</summary>
    public bool IsSuccess => Status is FileGenerationStatus.Success or FileGenerationStatus.NothingToDo;

    internal static FileGenerationResult Fail(string message) => new() { Status = FileGenerationStatus.Failed, Message = message };

    internal static FileGenerationResult Nothing(string message) => new() { Status = FileGenerationStatus.NothingToDo, Message = message };
}
