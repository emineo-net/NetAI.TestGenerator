namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>
///     Describes what a test skeleton is meant to do. Used by
///     <see cref="TestFrameworkProfile" /> to decide which using directives
///     and attributes a skeleton actually needs.
/// </summary>
public enum SkeletonPurpose
{
    /// <summary>
    ///     A real test method: instantiates the SUT, sets up mocks (if any),
    ///     calls the method, and asserts.
    /// </summary>
    FullTest,

    /// <summary>
    ///     A skip or refactor-first placeholder: declares a skip attribute and
    ///     describes required refactorings in comments. No mocks, no assertions.
    /// </summary>
    SkipTest
}