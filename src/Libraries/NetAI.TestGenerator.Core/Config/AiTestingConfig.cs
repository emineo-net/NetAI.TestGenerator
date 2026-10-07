using Newtonsoft.Json;

namespace NetAI.TestGenerator.Core.Config;

/// <summary>Contains the settings used to configure test generation.</summary>
public record AiTestingConfig
{
    /// <summary>Creates a configuration from its versioned settings sections.</summary>
    [JsonConstructor]
    public AiTestingConfig(string version, EnvironmentConfig environment, FrameworksConfig frameworks, CodeStyleConfig codeStyle,
        GenerationBehaviorConfig generationBehavior, LlmConnectionSettings aiConfiguration)
    {
        Version = version;
        Environment = environment;
        Frameworks = frameworks;
        CodeStyle = codeStyle;
        GenerationBehavior = generationBehavior;
        AiConfiguration = aiConfiguration;
    }

    /// <summary>Gets or sets the configuration format version.</summary>
    [JsonProperty("version")]
    public string Version { get; set; }

    /// <summary>Gets or sets the build configuration filter.</summary>
    [JsonProperty("buildConfigurationFilter")]
    public string BuildConfigurationFilter { get; set; }

    /// <summary>Gets or sets the maximum source length analyzed per method.</summary>
    [JsonProperty("maxMethodChars")]
    public int MaxMethodChars { get; set; } = 25_000;

    /// <summary>Gets or sets target framework and test project settings.</summary>
    [JsonProperty("environment")]
    public EnvironmentConfig Environment { get; set; }

    /// <summary>Gets or sets the test and mocking framework settings.</summary>
    [JsonProperty("frameworks")]
    public FrameworksConfig Frameworks { get; set; }

    /// <summary>Gets or sets generated-code formatting preferences.</summary>
    [JsonProperty("codeStyle")]
    public CodeStyleConfig CodeStyle { get; set; }

    /// <summary>Gets or sets test generation scope and organization settings.</summary>
    [JsonProperty("generationBehavior")]
    public GenerationBehaviorConfig GenerationBehavior { get; set; }

    /// <summary>Gets or sets the LLM connection, model, and system prompt settings.</summary>
    [JsonProperty("aiConfiguration")]
    public LlmConnectionSettings AiConfiguration { get; set; }
}

/// <summary>Defines the target .NET framework and generated test project name.</summary>
public record EnvironmentConfig
{
    /// <summary>Creates environment settings.</summary>
    [JsonConstructor]
    public EnvironmentConfig(string targetDotNetVersion, string testProjectName)
    {
        TargetDotNetVersion = targetDotNetVersion;
        TestProjectName = testProjectName;
    }

    /// <summary>Gets or sets the target framework moniker.</summary>
    public string TargetDotNetVersion { get; set; }

    /// <summary>Gets or sets the generated test project name.</summary>
    public string TestProjectName { get; set; }
}

/// <summary>Defines the test framework, mocking framework, and optional test-data helpers.</summary>
public record FrameworksConfig
{
    /// <summary>Initializes a frameworks config instance.</summary>
    [JsonConstructor]
    public FrameworksConfig(string testFramework, string mockingFramework, bool useFluentAssertions, bool useAutoFixture,
        bool verbosePrompt = true)
    {
        TestFramework = testFramework;
        MockingFramework = mockingFramework;
        UseFluentAssertions = useFluentAssertions;
        UseAutoFixture = useAutoFixture;
        VerbosePrompt = verbosePrompt;
    }

    /// <summary>Gets or sets the test framework.</summary>
    public string TestFramework { get; set; }

    /// <summary>Gets or sets the mocking framework.</summary>
    public string MockingFramework { get; set; }

    /// <summary>Gets or sets whether to use Fluent Assertions.</summary>
    public bool UseFluentAssertions { get; set; }

    /// <summary>Gets or sets whether to use AutoFixture.</summary>
    public bool UseAutoFixture { get; set; }

    /// <summary>When true, extended explanations (mode-specific rules, setup/assertion examples) are added to the AI prompt.</summary>
    public bool VerbosePrompt { get; set; } = true;
}

/// <summary>Defines formatting preferences for generated test code.</summary>
public record CodeStyleConfig
{
    /// <summary>Creates code-style settings.</summary>
    [JsonConstructor]
    public CodeStyleConfig(bool useFileScopedNamespace, bool useAsyncSuffix, int maxLineLength)
    {
        UseFileScopedNamespace = useFileScopedNamespace;
        UseAsyncSuffix = useAsyncSuffix;
        MaxLineLength = maxLineLength;
    }

    /// <summary>Gets or sets whether generated files use file-scoped namespaces.</summary>
    public bool UseFileScopedNamespace { get; set; }

    /// <summary>Gets or sets whether asynchronous test methods use the <c>Async</c> suffix.</summary>
    public bool UseAsyncSuffix { get; set; }

    /// <summary>Gets or sets the preferred maximum line length.</summary>
    public int MaxLineLength { get; set; }
}

/// <summary>Defines which kinds of tests to generate and how to organize them.</summary>
public record GenerationBehaviorConfig
{
    /// <summary>Creates test generation behavior settings.</summary>
    [JsonConstructor]
    public GenerationBehaviorConfig(string testStrategy, bool splitTestsByMethod, int maxTestsPerClass)
    {
        TestStrategy = testStrategy;
        SplitTestsByMethod = splitTestsByMethod;
        MaxTestsPerClass = maxTestsPerClass;
    }

    /// <summary>Gets or sets the generation scope: unit, integration, or both.</summary>
    public string TestStrategy { get; set; }

    /// <summary>Gets or sets whether to split tests into separate files by method.</summary>
    public bool SplitTestsByMethod { get; set; }

    /// <summary>Gets or sets the maximum number of test methods per generated class.</summary>
    public int MaxTestsPerClass { get; set; }
}