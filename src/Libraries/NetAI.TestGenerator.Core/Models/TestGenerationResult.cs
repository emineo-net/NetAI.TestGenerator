namespace DotNet10TestGenerator;

/// <summary>Reports the outcome, diagnostics, and generated source from test project validation.</summary>
public sealed class TestGenerationResult
{

    /// <summary>Initializes a test generation result instance.</summary>
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

    /// <summary>Gets whether test generation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the message.</summary>
    public string Message { get; }

    /// <summary>Gets the compiler errors.</summary>
    public string[]? CompilerErrors { get; }

    /// <summary>Gets the exception details.</summary>
    public string[]? ExceptionDetails { get; }

    /// <summary>Gets whether the generated test must be regenerated.</summary>
    public bool RequiresRegeneration { get; }

    /// <summary>Gets whether an environment issue caused the failure.</summary>
    public bool IsEnvironmentIssue { get; }

    /// <summary>Gets the test class path.</summary>
    public string? TestClassPath { get; }

    /// <summary>Gets the test class code.</summary>
    public string? TestClassCode { get; }
}