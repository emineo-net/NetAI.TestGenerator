namespace NetAI.TestGenerator.Core.Models.Enums;

/// <summary>Identifies the mocking library used when preparing generated tests.</summary>
public enum MockFramework
{

    /// <summary>No mocking library has been selected.</summary>
    Unknown,

    /// <summary>Moq mocking library.</summary>
    Moq,

    /// <summary>NSubstitute mocking library.</summary>
    NSubstitute,

    /// <summary>FakeItEasy mocking library.</summary>
    FakeItEasy
}