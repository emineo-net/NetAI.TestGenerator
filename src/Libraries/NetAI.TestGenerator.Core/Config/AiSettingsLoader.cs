using Newtonsoft.Json;

namespace NetAI.TestGenerator.Core.Config;

public static class AiSettingsLoader
{
    public static AiTestingConfig Load(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            throw new ArgumentException("ProjectDir must not be empty.", nameof(projectDir));
        }

        var path = Path.Combine(projectDir, "aisettings.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"aisettings.json was not found in '{projectDir}'.", path);
        }

        var json = File.ReadAllText(path);

        var config = JsonConvert.DeserializeObject<AiTestingConfig>(json);

        if (config is null)
        {
            throw new InvalidOperationException($"aisettings.json at '{path}' could not be parsed.");
        }

        return config;
    }
}