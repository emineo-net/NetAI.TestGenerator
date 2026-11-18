using Newtonsoft.Json;

namespace NetAI.TestGenerator.Core.Config;

/// <summary>Loads and deserializes an <c>aisettings.json</c> file.</summary>
public static class AiSettingsLoader
{
    /// <summary>The settings file name.</summary>
    public const string FileName = "aisettings.json";

    /// <summary>
    /// Loads the AI testing configuration. The file is searched in <paramref name="projectDir"/> and then in its parent
    /// directories up to (and including) the directory containing the solution file.
    /// </summary>
    /// <exception cref="FileNotFoundException">No settings file was found.</exception>
    /// <exception cref="InvalidOperationException">The file could not be parsed or contains invalid AI settings.</exception>
    public static AiTestingConfig Load(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            throw new ArgumentException("ProjectDir must not be empty.", nameof(projectDir));
        }

        var path = Find(projectDir)
                   ?? throw new FileNotFoundException($"{FileName} was not found in '{projectDir}' or its parent directories.",
                       Path.Combine(projectDir, FileName));

        return LoadFile(path);
    }

    /// <summary>Loads and validates a specific settings file.</summary>
    public static AiTestingConfig LoadFile(string path)
    {
        AiTestingConfig? config;
        try
        {
            config = JsonConvert.DeserializeObject<AiTestingConfig>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{FileName} at '{path}' is not valid JSON: {ex.Message}", ex);
        }

        if (config is null)
        {
            throw new InvalidOperationException($"{FileName} at '{path}' could not be parsed.");
        }

        config.AiConfiguration?.Validate();
        return config;
    }

    /// <summary>Returns the nearest settings file, stopping at the solution directory; <c>null</c> if none exists.</summary>
    public static string? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
            {
                break;
            }
        }

        return null;
    }
}
