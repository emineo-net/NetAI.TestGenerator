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
        "mockingFramework": "unknown",
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
        var path = explicitPath is not null
            ? Path.GetFullPath(explicitPath)
            : Discover(startDirectory);

        var json = path is not null && File.Exists(path)
            ? File.ReadAllText(path)
            : DefaultJson;

        return JsonConvert.DeserializeObject<AiTestingConfig>(json)
               ?? JsonConvert.DeserializeObject<AiTestingConfig>(DefaultJson)!;
    }

    private static string? Discover(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "aisettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}