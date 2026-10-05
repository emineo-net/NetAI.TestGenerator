using NetAI.TestGenerator.Core.Analysis;

namespace NetAI.TestGenerator.Core.Models;

/// <summary>
///     Controls which framework types and compilation diagnostics appear in testability reports,
///     and carries the framework profile used for skeleton generation.
/// </summary>
public sealed class AnalyzerOptions
{
    // ---------------------------------------------------------------- legacy flags

    /// <summary>
    ///     Legacy flag: whether framework types such as <c>System.String</c> are included
    ///     in the analysis result. The current analyzer already drops BCL types aggressively
    ///     (see <c>RoslynDllTestabilityAnalyzer.CollectReferencedTypes</c>), so this flag
    ///     no longer changes behaviour but is kept for API compatibility.
    /// </summary>
    public bool IncludeFrameworkTypes { get; init; } = true;

    // ---------------------------------------------------------------- diagnostics

    /// <summary>Maximum number of compilation diagnostics included in a report.</summary>
    public int MaxDiagnostics { get; init; } = 25;

    // ---------------------------------------------------------------- call graph

    /// <summary>
    ///     Legacy flag: maximum depth of the analyzed call graph. The current analyzer emits
    ///     a flat one-line call graph (the method itself); kept for API compatibility.
    /// </summary>
    public int MaxCallGraphDepth { get; init; } = 3;

    /// <summary>
    ///     Legacy flag: whether recursive calls are followed. Current analyzer does not walk
    ///     the graph recursively; kept for API compatibility.
    /// </summary>
    public bool IncludeRecursiveCallGraph { get; init; } = true;

    // ---------------------------------------------------------------- skeleton profile

    /// <summary>
    ///     Optional framework profile used to build the test skeleton. When null, the analyzer
    ///     falls back to a default profile (xUnit + Moq + FluentAssertions).
    /// </summary>
    public TestFrameworkProfile? TestFrameworkProfile { get; init; }
}