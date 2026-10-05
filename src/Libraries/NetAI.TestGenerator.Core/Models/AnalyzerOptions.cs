using NetAI.TestGenerator.Core.Analysis;

namespace NetAI.TestGenerator.Core.Models;

/// <summary>Configures testability analysis and test skeleton generation.</summary>
public sealed class AnalyzerOptions
{

    /// <summary>Legacy flag: whether framework types such as System.String are included in the analysis result.</summary>
    public bool IncludeFrameworkTypes { get; init; } = true;

    /// <summary>Maximum number of compilation diagnostics included in a report.</summary>
    public int MaxDiagnostics { get; init; } = 25;

    /// <summary>Legacy flag: maximum depth of the analyzed call graph.</summary>
    public int MaxCallGraphDepth { get; init; } = 3;

    /// <summary>Legacy flag: whether recursive calls are followed.</summary>
    public bool IncludeRecursiveCallGraph { get; init; } = true;

    /// <summary>Optional framework profile used to build the test skeleton.</summary>
    public TestFrameworkProfile? TestFrameworkProfile { get; init; }
}