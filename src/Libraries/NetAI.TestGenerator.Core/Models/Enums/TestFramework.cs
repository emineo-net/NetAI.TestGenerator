namespace NetAI.TestGenerator.Core.Models.Enums;

/// <summary>Identifies the test framework used to format or validate generated tests.</summary>
public enum TestFramework
{

    /// <summary>No test framework has been selected.</summary>
    Unknown,

    /// <summary>NUnit test framework.</summary>
    NUnit,

    /// <summary>xUnit test framework.</summary>
    xUnit,

    /// <summary>Microsoft Test Framework (MSTest).</summary>
    MSTest
}