using NetAI.TestGenerator.Core.Models.Enums;
using Scriban;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Renders focused prompts for repairing generated test code after compilation errors.</summary>
public class AiPromptBuilderSimple
{
    /// <summary>Builds a repair prompt that includes the generated code and compiler errors.</summary>
    /// <param name="generierter_code">The generated C# code that failed to compile.</param>
    /// <param name="compiler_fehler">Compiler diagnostics describing the failure.</param>
    /// <param name="testFramework">Selected test framework, used to steer the repair prompt.</param>
    /// <param name="mockFramework">Selected mocking framework, used to steer the repair prompt.</param>
    /// <returns>A rendered prompt for repairing the code.</returns>
    public string FixUnittestPrompt(string generierter_code, string compiler_fehler, TestFramework testFramework = TestFramework.xUnit,
        MockFramework mockFramework = MockFramework.Unknown)
    {
        var template = Template.Parse(PromptTemplates.UnitTestFixer);

        var kontext = new Dictionary<string, object>
        {
            { "generierter_code", generierter_code },
            { "compiler_fehler", compiler_fehler },
            { "test_framework", GetTestFrameworkName(testFramework) },
            { "test_framework_namespace", GetTestFrameworkNamespace(testFramework) },
            { "test_attribute", GetTestAttribute(testFramework) },
            { "mock_framework", GetMockFrameworkName(mockFramework) },
            { "mock_framework_namespace", GetMockFrameworkNamespace(mockFramework) ?? string.Empty }
        };

        return template.Render(kontext);
    }

    /// <summary>Builds a concise repair prompt from the current code and compiler errors.</summary>
    /// <param name="compiler_fehler">Compiler diagnostics describing the failure.</param>
    /// <param name="aktuellerCode">The current C# code to repair.</param>
    /// <param name="testFramework">Selected test framework, used to steer the repair prompt.</param>
    /// <param name="mockFramework">Selected mocking framework, used to steer the repair prompt.</param>
    /// <returns>A rendered prompt that requests the complete corrected code.</returns>
    public string FixUnittestPromptSimple(string compiler_fehler, string aktuellerCode, TestFramework testFramework = TestFramework.xUnit,
        MockFramework mockFramework = MockFramework.Unknown)
    {
        var template = Template.Parse(PromptTemplates.UnitTestFixerSimple);

        var kontext = new Dictionary<string, object>
        {
            { "compiler_fehler", compiler_fehler },
            { "aktuellerCode", aktuellerCode },
            { "test_framework", GetTestFrameworkName(testFramework) },
            { "test_framework_namespace", GetTestFrameworkNamespace(testFramework) },
            { "test_attribute", GetTestAttribute(testFramework) },
            { "mock_framework", GetMockFrameworkName(mockFramework) },
            { "mock_framework_namespace", GetMockFrameworkNamespace(mockFramework) ?? string.Empty }
        };

        return template.Render(kontext);
    }

    // --- Framework-Mapping (bewusst lokal, um die Kopplung an andere Services zu vermeiden) ---

    private static string GetTestFrameworkName(TestFramework testFramework)
    {
        return testFramework switch
        {
            TestFramework.NUnit => "NUnit",
            TestFramework.MSTest => "MSTest",
            TestFramework.xUnit => "xUnit",
            _ => "xUnit"
        };
    }

    private static string GetTestFrameworkNamespace(TestFramework testFramework)
    {
        return testFramework switch
        {
            TestFramework.NUnit => "NUnit.Framework",
            TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting",
            TestFramework.xUnit => "Xunit",
            _ => "Xunit"
        };
    }

    private static string GetTestAttribute(TestFramework testFramework)
    {
        return testFramework switch
        {
            TestFramework.NUnit => "[Test]",
            TestFramework.MSTest => "[TestMethod]",
            TestFramework.xUnit => "[Fact]",
            _ => "[Fact]"
        };
    }

    private static string GetMockFrameworkName(MockFramework mockFramework)
    {
        return mockFramework switch
        {
            MockFramework.Moq => "Moq",
            MockFramework.NSubstitute => "NSubstitute",
            MockFramework.FakeItEasy => "FakeItEasy",
            _ => "none"
        };
    }

    private static string? GetMockFrameworkNamespace(MockFramework mockFramework)
    {
        return mockFramework switch
        {
            MockFramework.Moq => "Moq",
            MockFramework.NSubstitute => "NSubstitute",
            MockFramework.FakeItEasy => "FakeItEasy",
            _ => null
        };
    }
}