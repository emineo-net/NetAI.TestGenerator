namespace NetAI.TestGenerator.Core.Models;

/// <summary>Controls which framework types and compilation diagnostics appear in testability reports.</summary>
public sealed class AnalyzerOptions
{
    /// <summary>Gets whether framework types such as <c>System.String</c> are included in analysis results.</summary>
    public bool IncludeFrameworkTypes { get; init; } = true;

    /// <summary>Gets the maximum number of compilation diagnostics included in a report.</summary>
    public int MaxDiagnostics { get; init; } = 25;

    // NEW:
    public int MaxCallGraphDepth { get; init; } = 3;
    public bool IncludeRecursiveCallGraph { get; init; } = true;
}