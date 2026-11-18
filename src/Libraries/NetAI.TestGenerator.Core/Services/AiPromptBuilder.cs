using System.Text;
using NetAI.TestGenerator.Core.Extensions;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models.Enums;
using Scriban;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Builds test-generation prompts from project settings and source code.</summary>
public class AiPromptBuilder
{
    private readonly string _configFilePath;

    /// <summary>Creates a prompt builder using the specified settings file.</summary>
    public AiPromptBuilder(string configFilePath = "aisettings.json")
    {
        _configFilePath = configFilePath;
    }

    /// <summary>Builds system instructions from the configured framework, style, and generation strategy.</summary>
    public Task<string> BuildSystemPromptAsync()
    {
        var config = AiSettingsLoader.Load(Path.GetDirectoryName(Path.GetFullPath(_configFilePath))!);

        var framework = config.Frameworks.TestFramework.ToLowerInvariant() switch
        {
            "nunit" => "NUnit",
            "mstest" => "MSTest",
            _ => "xUnit"
        };

        var mocker = (config.Frameworks.MockingFramework ?? string.Empty).ToLowerInvariant() switch
        {
            "nsubstitute" or "nsub" => "NSubstitute",
            "fakeiteasy" => "FakeItEasy",
            "moq" => "Moq",
            _ => "hand-written test doubles (no mocking library is configured)"
        };

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(config.AiConfiguration.SystemPrompt);
        promptBuilder.AppendLine();

        promptBuilder.AppendLine($"""
                                  ### TECHNICAL SPECIFICATIONS:
                                  - .NET Target Version: {config.Environment.TargetDotNetVersion}
                                  - Test Framework: {framework}
                                  - Namespace Style: {(config.CodeStyle.UseFileScopedNamespace ? "File-scoped (namespace X;)" : "Block-scoped (namespace X { })")}
                                  - Maximum Line Length: {config.CodeStyle.MaxLineLength} characters
                                  """);

        var unitTestRules = $"""
                             ### UNIT TESTING RULES:
                             - Focus: Test the class in complete isolation.
                             - Isolation: Mock ALL external dependencies, services, and contexts using {mocker}.
                             - Test Data Engine: {(config.Frameworks.UseAutoFixture ? "Use AutoFixture for anonymous data." : "Instantiate objects manually.")}
                             - Assertions: Use FluentAssertions: {(config.Frameworks.UseFluentAssertions ? "Yes" : "No")}.
                             """;

        var integrationTestRules = """
                                   ### INTEGRATION TESTING RULES:
                                   - Focus: Test the interaction between multiple components or infrastructure layers.
                                   - Infrastructure: DO NOT mock internal business logic. Use 'WebApplicationFactory' for API testing.
                                   - Database: Use real test providers or in-memory databases. Do not mock data repositories.
                                   """;

        switch (config.GenerationBehavior.TestStrategy.ToLowerInvariant())
        {
            case "integration":
                promptBuilder.AppendLine("\n### SCOPE: GENERATE INTEGRATION TESTS ONLY");
                promptBuilder.AppendLine(integrationTestRules);
                break;

            case "both":
                promptBuilder.AppendLine("\n### SCOPE: GENERATE BOTH UNIT AND INTEGRATION TESTS");
                promptBuilder.AppendLine("You MUST generate two distinct sets of tests for this class.");
                promptBuilder.AppendLine("1. Provide Unit Tests (isolated via mocks).");
                promptBuilder.AppendLine("2. Provide Integration Tests (using real components/pipelines).");
                promptBuilder.AppendLine("Keep them clearly separated (e.g., using separate test classes or clear naming conventions).");
                promptBuilder.AppendLine();
                promptBuilder.AppendLine(unitTestRules);
                promptBuilder.AppendLine();
                promptBuilder.AppendLine(integrationTestRules);
                break;

            default:
                promptBuilder.AppendLine("\n### SCOPE: GENERATE UNIT TESTS ONLY");
                promptBuilder.AppendLine(unitTestRules);
                break;
        }

        promptBuilder.AppendLine($"""

                                  ### STRUCTURE & SCOPE:
                                  - Structure: {(config.GenerationBehavior.SplitTestsByMethod ? "Create a dedicated test class for the specific method." : "Generate a unified test class structure.")}
                                  - Maximum Tests per Class: {config.GenerationBehavior.MaxTestsPerClass}
                                  """);

        return Task.FromResult(promptBuilder.ToString());
    }

    /// <summary>Builds a prompt that generates a unit test for the supplied method.</summary>
    public string GeneratePrompt(string klassenCode, string methodenName, string methodenSignatur,
        TestFramework testFramework = TestFramework.xUnit, MockFramework mockFramework = MockFramework.Unknown)
    {
        var template = Template.Parse(PromptTemplates.UnitTestGenerator);

        var kontext = new Dictionary<string, object>
        {
            { "test_framework", OrDefault(testFramework).DisplayName() },
            { "test_framework_namespace", OrDefault(testFramework).Namespace() },
            { "test_attribute", OrDefault(testFramework).TestAttribute() },
            { "mocking_library", GetMockFrameworkName(mockFramework) },
            { "mock_framework_namespace", mockFramework.Namespace() },
            { "ziel_methode_name", methodenName },
            { "ziel_methode_signatur", methodenSignatur },
            { "klassen_code", klassenCode }
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
