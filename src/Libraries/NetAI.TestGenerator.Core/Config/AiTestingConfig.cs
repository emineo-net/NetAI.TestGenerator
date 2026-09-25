using Newtonsoft.Json;

namespace NetAI.TestGenerator.Core.Config;

public record AiTestingConfig
{
    [JsonConstructor]
    public AiTestingConfig(string version, EnvironmentConfig environment, FrameworksConfig frameworks, CodeStyleConfig codeStyle,
        GenerationBehaviorConfig generationBehavior, AiConfigurationConfig aiConfiguration)
    {
        Version = version;
        Environment = environment;
        Frameworks = frameworks;
        CodeStyle = codeStyle;
        GenerationBehavior = generationBehavior;
        AiConfiguration = aiConfiguration;
    }

    [JsonProperty("version")] public string Version { get; set; }

    [JsonProperty("environment")] public EnvironmentConfig Environment { get; set; }

    [JsonProperty("frameworks")] public FrameworksConfig Frameworks { get; set; }

    [JsonProperty("codeStyle")] public CodeStyleConfig CodeStyle { get; set; }

    [JsonProperty("generationBehavior")] public GenerationBehaviorConfig GenerationBehavior { get; set; }

    [JsonProperty("aiConfiguration")] public AiConfigurationConfig AiConfiguration { get; set; }

}

public record EnvironmentConfig
{
    [JsonConstructor]
    public EnvironmentConfig(string targetDotNetVersion, string testProjectName)
    {
        TargetDotNetVersion = targetDotNetVersion;
        TestProjectName = testProjectName;
    }

    public string TargetDotNetVersion { get; set; }
    public string TestProjectName { get; set; }
}

public record FrameworksConfig
{
    [JsonConstructor]
    public FrameworksConfig(string testFramework, string mockingFramework, bool useFluentAssertions, bool useAutoFixture)
    {
        TestFramework = testFramework;
        MockingFramework = mockingFramework;
        UseFluentAssertions = useFluentAssertions;
        UseAutoFixture = useAutoFixture;
    }

    public string TestFramework { get; set; }
    public string MockingFramework { get; set; }
    public bool UseFluentAssertions { get; set; }
    public bool UseAutoFixture { get; set; }
}

public record CodeStyleConfig
{
    [JsonConstructor]
    public CodeStyleConfig(bool useFileScopedNamespace, bool useAsyncSuffix, int maxLineLength)
    {
        UseFileScopedNamespace = useFileScopedNamespace;
        UseAsyncSuffix = useAsyncSuffix;
        MaxLineLength = maxLineLength;
    }

    public bool UseFileScopedNamespace { get; set; }
    public bool UseAsyncSuffix { get; set; }
    public int MaxLineLength { get; set; }
}

public record GenerationBehaviorConfig
{
    [JsonConstructor]
    public GenerationBehaviorConfig(string testStrategy, bool splitTestsByMethod, int maxTestsPerClass)
    {
        TestStrategy = testStrategy;
        SplitTestsByMethod = splitTestsByMethod;
        MaxTestsPerClass = maxTestsPerClass;
    }

    public string TestStrategy { get; set; }
    public bool SplitTestsByMethod { get; set; }
    public int MaxTestsPerClass { get; set; }
}

public record AiConfigurationConfig
{
    [JsonConstructor]
    public AiConfigurationConfig(string model, double temperature, string systemPrompt)
    {
        Model = model;
        Temperature = temperature;
        SystemPrompt = systemPrompt;
    }

    public string Model { get; set; }
    public double Temperature { get; set; }
    public string SystemPrompt { get; set; }
}