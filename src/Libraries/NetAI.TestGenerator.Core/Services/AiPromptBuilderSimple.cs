using NetAI.TestGenerator.Core.Models.Enums;
using NetAI.TestGenerator.Core.Extensions;
using Scriban;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Renders focused prompts for repairing generated test code after compilation errors.</summary>
public class AiPromptBuilderSimple
{
    /// <summary>Builds a repair prompt that includes the generated code and compiler errors.</summary>
    public string FixUnittestPrompt(string generierter_code, string compiler_fehler, TestFramework testFramework = TestFramework.xUnit,
        MockFramework mockFramework = MockFramework.Unknown)
    {
        var template = Template.Parse(PromptTemplates.UnitTestFixer);

        var kontext = new Dictionary<string, object>
        {
            { "generierter_code", generierter_code },
            { "compiler_fehler", compiler_fehler },
            { "test_framework", OrDefault(testFramework).DisplayName() },
            { "test_framework_namespace", OrDefault(testFramework).Namespace() },
            { "test_attribute", OrDefault(testFramework).TestAttribute() },
            { "mock_framework", GetMockFrameworkName(mockFramework) },
            { "mock_framework_namespace", mockFramework.Namespace() }
        };

        return template.Render(kontext);
    }

    /// <summary>Builds a concise repair prompt from the current code and compiler errors.</summary>
    public string FixUnittestPromptSimple(string compiler_fehler, string aktuellerCode, TestFramework testFramework = TestFramework.xUnit,
        MockFramework mockFramework = MockFramework.Unknown)
    {
        var template = Template.Parse(PromptTemplates.UnitTestFixerSimple);

        var kontext = new Dictionary<string, object>
        {
            { "compiler_fehler", compiler_fehler },
            { "aktuellerCode", aktuellerCode },
            { "test_framework", OrDefault(testFramework).DisplayName() },
            { "test_framework_namespace", OrDefault(testFramework).Namespace() },
            { "test_attribute", OrDefault(testFramework).TestAttribute() },
            { "mock_framework", GetMockFrameworkName(mockFramework) },
            { "mock_framework_namespace", mockFramework.Namespace() }
        };

        return template.Render(kontext);
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

    /// <summary>Prompts fall back to xUnit when no framework is configured.</summary>
    private static TestFramework OrDefault(TestFramework framework) => framework == TestFramework.Unknown ? TestFramework.xUnit : framework;
}
