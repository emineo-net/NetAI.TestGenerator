namespace DotNet10TestGenerator;

/// <summary>Reports the outcome, diagnostics, and generated source from test project validation.</summary>
public sealed class TestGenerationResult
{
    /// <summary>Creates a result for test generation and validation.</summary>
    /// <param name="isSuccess">Whether generation and validation succeeded.</param>
    /// <param name="message">Human-readable operation summary.</param>
    /// <param name="compilerErrors">Optional compiler or build errors.</param>
    /// <param name="exceptionDetails">Optional exception details.</param>
    /// <param name="requiresRegeneration">Whether the test code should be regenerated.</param>
    /// <param name="isEnvironmentIssue">Whether the failure was caused by the environment.</param>
    /// <param name="testClassPath">Path to the generated test class.</param>
    /// <param name="testClassCode">Generated or repaired test source code.</param>
    public TestGenerationResult(bool isSuccess, string message, string[]? compilerErrors = null, string[]? exceptionDetails = null,
        bool requiresRegeneration = false, bool isEnvironmentIssue = false, string? testClassPath = null, string? testClassCode = null)
    {
        IsSuccess = isSuccess;
        Message = message;
        CompilerErrors = compilerErrors;
        ExceptionDetails = exceptionDetails;
        RequiresRegeneration = requiresRegeneration;
        IsEnvironmentIssue = isEnvironmentIssue;
        TestClassPath = testClassPath;
        TestClassCode = testClassCode;
    }

    /// <summary>Gets whether the generated test project compiled successfully.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets a human-readable summary of the operation.</summary>
    public string Message { get; }

    /// <summary>Gets compiler or build errors, if any.</summary>
    public string[]? CompilerErrors { get; }

    /// <summary>Gets additional exception details, if available.</summary>
    public string[]? ExceptionDetails { get; }

    /// <summary>Gets whether the generated test should be regenerated because of semantic errors.</summary>
    public bool RequiresRegeneration { get; }

    /// <summary>Gets whether validation failed because of an environmental issue rather than test code.</summary>
    public bool IsEnvironmentIssue { get; }

    /// <summary>Gets the path to the generated test class, when one was written.</summary>
    public string? TestClassPath { get; }

    /// <summary>Gets the generated or repaired test class source code.</summary>
    public string? TestClassCode { get; }
}