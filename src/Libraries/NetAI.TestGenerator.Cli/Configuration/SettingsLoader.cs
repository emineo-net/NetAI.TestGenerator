using NetAI.TestGenerator.Core.Config;
using Newtonsoft.Json;

namespace NetAI.TestGenerator.Cli.Configuration;

/// <summary>
/// Loads <see cref="AiTestingConfig"/> from aisettings.json (discovery or explicit path).
/// Falls back to sensible defaults when no file is found.
/// </summary>
public static class SettingsLoader
{
    private const string DefaultJson = """
    {
      "version": "1.1",
      "buildConfigurationFilter": "all",
      "maxMethodChars": 25000,
      "environment": {
        "targetDotNetVersion": "net9.0",
        "testProjectName": "{ProjectName}.Tests"
      },
      "frameworks": {
        "testFramework": "xunit",
        "mockingFramework": "none",
        "useFluentAssertions": true,
        "useAutoFixture": false,
        "verbosePrompt": true
      },
      "codeStyle": {
        "useFileScopedNamespace": true,
        "useAsyncSuffix": true,
        "maxLineLength": 120
      },
      "generationBehavior": {
        "testStrategy": "Both",
        "splitTestsByMethod": false,
        "maxTestsPerClass": 15
      }
    }
    """;

    public static AiTestingConfig Load(string? explicitPath, string startDirectory)
    {
        string? path;
        if (explicitPath is not null)
        {
            // An explicitly given file must exist; silently falling back to defaults hides typos.
            path = Path.GetFullPath(explicitPath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Config file '{explicitPath}' was not found.", path);
        }
        else
        {
            // Same discovery rule as the MSBuild task: project directory upward to the solution directory.
            path = AiSettingsLoader.Find(startDirectory);
        }

        return path is not null
            ? AiSettingsLoader.LoadFile(path)
            : JsonConvert.DeserializeObject<AiTestingConfig>(DefaultJson)!;
    }
}