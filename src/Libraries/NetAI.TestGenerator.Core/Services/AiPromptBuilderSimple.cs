using Scriban;

namespace NetAI.TestGenerator.Core.Services;

public class AiPromptBuilderSimple
{

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

