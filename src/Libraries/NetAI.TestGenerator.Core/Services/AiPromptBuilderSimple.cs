using Scriban;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Renders focused prompts for repairing generated test code after compilation errors.</summary>
public class AiPromptBuilderSimple
{

    /// <summary>Builds a repair prompt that includes the generated code and compiler errors.</summary>
    /// <param name="generierter_code">The generated C# code that failed to compile.</param>
    /// <param name="compiler_fehler">Compiler diagnostics describing the failure.</param>
    /// <returns>A rendered prompt for repairing the code.</returns>
    public string FixUnittestPrompt(string generierter_code, string compiler_fehler)
    {
        var template = Template.Parse(PromptTemplates.UnitTestFixer);

        var kontext = new Dictionary<string, object>
        {
            { "generierter_code", generierter_code },
            { "compiler_fehler", compiler_fehler },
        };

        return template.Render(kontext);
    }

    /// <summary>Builds a concise repair prompt from the current code and compiler errors.</summary>
    /// <param name="compiler_fehler">Compiler diagnostics describing the failure.</param>
    /// <param name="aktuellerCode">The current C# code to repair.</param>
    /// <returns>A rendered prompt that requests the complete corrected code.</returns>
    public string FixUnittestPromptSimple(string compiler_fehler, string aktuellerCode)
    {
        var template = Template.Parse(PromptTemplates.UnitTestFixerSimple);

        var kontext = new Dictionary<string, object>
        {
            { "compiler_fehler", compiler_fehler },
            { "aktuellerCode", aktuellerCode },
        };

        return template.Render(kontext);
    }

}

