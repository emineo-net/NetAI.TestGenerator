using System.Text;
using NetAI.TestGenerator.Core.Config;
using Scriban;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Builds test-generation prompts from project settings and source code.</summary>
public class AiPromptBuilder
{
    private readonly string _configFilePath;

    /// <summary>Creates a prompt builder using the specified settings file.</summary>
    /// <param name="configFilePath">Path to <c>aisettings.json</c>; defaults to the current directory.</param>
    public AiPromptBuilder(string configFilePath = "aisettings.json")
    {
        _configFilePath = configFilePath;
    }

    /// <summary>Builds system instructions from the configured framework, style, and generation strategy.</summary>
    /// <returns>A task whose result is the generated system prompt.</returns>
    /// <exception cref="FileNotFoundException">The settings file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The settings file cannot be parsed.</exception>
    public Task<string> BuildSystemPromptAsync()
    {
        var config = AiSettingsLoader.Load(Path.GetDirectoryName(Path.GetFullPath(_configFilePath))!);

        var framework = config.Frameworks.TestFramework.ToLowerInvariant() switch
        {
            "nunit" => "NUnit",
            "mstest" => "MSTest",
            _ => "xUnit"
        };

        var mocker = config.Frameworks.MockingFramework.ToLowerInvariant() switch
        {
            "nsubstitute" or "nsub" => "NSubstitute",
            _ => "Moq"
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


    /// <summary>Creates a unit-test prompt for a specific method and its containing class.</summary>
    /// <param name="klassenCode">Source code for the class that contains the method.</param>
    /// <param name="methodenName">Name of the method to test.</param>
    /// <param name="methodenSignatur">Signature or search term that identifies the method.</param>
    /// <returns>A rendered prompt containing the class context and test-generation rules.</returns>
    public string GeneratePrompt(string klassenCode, string methodenName, string methodenSignatur)
    {
        var template = Template.Parse(PromptTemplates.UnitTestGenerator);

        var kontext = new Dictionary<string, object>
        {
            { "test_framework", "xUnit" },
            { "mocking_library", "NSubstitute" },
            { "ziel_methode_name", methodenName },
            { "ziel_methode_signatur", methodenSignatur },
            { "klassen_code", klassenCode }
        };

        return template.Render(kontext);
    }




}