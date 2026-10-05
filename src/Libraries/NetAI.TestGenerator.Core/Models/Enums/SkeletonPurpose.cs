namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>Specifies the purpose of a generated test skeleton.</summary>
public enum SkeletonPurpose
{

    /// <summary>Generates an executable test with setup, method call, and assertions.</summary>
    FullTest,

    /// <summary>Generates a skipped test without setup or assertions.</summary>
    SkipTest
}