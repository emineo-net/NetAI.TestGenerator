using NetAI.ResxTranslator.Core.Config;
using System.Text;

namespace NetAI.ResxTranslator.Core.Services;

public class AiPromptBuilder
{
    private readonly string _configFilePath;

    public AiPromptBuilder(string configFilePath = "aisettings.json") => _configFilePath = configFilePath;

    public Task<string> BuildSystemPromptAsync()
    {
        var config = AiSettingsLoader.Load(Path.GetDirectoryName(Path.GetFullPath(_configFilePath))!);

        // Fehlerresistente Auswertung der Strings
        string framework = config.Frameworks.TestFramework.ToLowerInvariant() switch
        {
            "nunit" => "NUnit",
            "mstest" => "MSTest",
            _ => "xUnit" // Sicherer Fallback, falls jemand Tippfehler macht
        };

        string mocker = config.Frameworks.MockingFramework.ToLowerInvariant() switch
        {
            "nsubstitute" or "nsub" => "NSubstitute",
            _ => "Moq" // Sicherer Fallback
        };

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(config.AiConfiguration.SystemPrompt);
        promptBuilder.AppendLine();

        // Technische Basisdaten
        promptBuilder.AppendLine($"""
### TECHNICAL SPECIFICATIONS:
- .NET Target Version: {config.Environment.TargetDotNetVersion}
- Test Framework: {framework}
- Namespace Style: {(config.CodeStyle.UseFileScopedNamespace ? "File-scoped (namespace X;)" : "Block-scoped (namespace X { })")}
- Maximum Line Length: {config.CodeStyle.MaxLineLength} characters
""");

        // Definitionen der Regelblöcke als wiederverwendbare Strings
        string unitTestRules = $"""
### UNIT TESTING RULES:
- Focus: Test the class in complete isolation.
- Isolation: Mock ALL external dependencies, services, and contexts using {mocker}.
- Test Data Engine: {(config.Frameworks.UseAutoFixture ? "Use AutoFixture for anonymous data." : "Instantiate objects manually.")}
- Assertions: Use FluentAssertions: {(config.Frameworks.UseFluentAssertions ? "Yes" : "No")}.
""";

        string integrationTestRules = $"""
### INTEGRATION TESTING RULES:
- Focus: Test the interaction between multiple components or infrastructure layers.
- Infrastructure: DO NOT mock internal business logic. Use 'WebApplicationFactory' for API testing.
- Database: Use real test providers or in-memory databases. Do not mock data repositories.
""";

        // DIE DREIFACH-WEICHE
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

            default: // "unit" oder falls sich jemand vertippt hat
                promptBuilder.AppendLine("\n### SCOPE: GENERATE UNIT TESTS ONLY");
                promptBuilder.AppendLine(unitTestRules);
                break;
        }

        // Struktur-Regeln anhängen
        promptBuilder.AppendLine($"""

### STRUCTURE & SCOPE:
- Structure: {(config.GenerationBehavior.SplitTestsByMethod ? "Create a dedicated test class for the specific method." : "Generate a unified test class structure.")}
- Maximum Tests per Class: {config.GenerationBehavior.MaxTestsPerClass}
""");

        return Task.FromResult(promptBuilder.ToString());
    }
}