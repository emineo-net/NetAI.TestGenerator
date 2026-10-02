namespace DotNet10TestGenerator;

/// <summary>Reports the outcome, diagnostics, and generated source from test project validation.</summary>
public sealed class TestGenerationResult
{
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

    public bool IsSuccess { get; }

    public string Message { get; }

    public string[]? CompilerErrors { get; }

    public string[]? ExceptionDetails { get; }

    public bool RequiresRegeneration { get; }

    public bool IsEnvironmentIssue { get; }

    public string? TestClassPath { get; }

    public string? TestClassCode { get; }
}