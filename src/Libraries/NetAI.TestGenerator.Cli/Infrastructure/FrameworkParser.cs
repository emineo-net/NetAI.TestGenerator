using NetAI.TestGenerator.Core.Models.Enums;

namespace NetAI.TestGenerator.Cli.Infrastructure;

public static class FrameworkParser
{
    public static TestFramework ParseTest(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return TestFramework.xUnit;

        return name.Trim().ToLowerInvariant() switch
        {
            "xunit" => TestFramework.xUnit,
            "nunit" => TestFramework.NUnit,
            "mstest" => TestFramework.MSTest,
            _ => throw new ArgumentException(
                $"Unsupported test framework '{name}'. Use xunit, nunit, or mstest.", nameof(name))
        };
    }

    public static MockFramework ParseMock(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return MockFramework.Unknown;

        return name.Trim().ToLowerInvariant() switch
        {
            "none" or "unknown" => MockFramework.Unknown,
            "moq" => MockFramework.Moq,
            "nsubstitute" => MockFramework.NSubstitute,
            "fakeiteasy" => MockFramework.FakeItEasy,
            _ => throw new ArgumentException(
                $"Unsupported mocking framework '{name}'. Use moq, nsubstitute, fakeiteasy, or none.", nameof(name))
        };
    }
}