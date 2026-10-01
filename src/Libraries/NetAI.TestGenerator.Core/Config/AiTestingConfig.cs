using Newtonsoft.Json;

namespace NetAI.TestGenerator.Core.Config;

/// <summary>Contains the settings used to configure test generation.</summary>
public record AiTestingConfig
{
    /// <summary>Creates a configuration from its versioned settings sections.</summary>
    /// <param name="version">Configuration format version.</param>
    /// <param name="environment">Target framework and test project naming settings.</param>
    /// <param name="frameworks">Test, mocking, and assertion framework settings.</param>
    /// <param name="codeStyle">Formatting preferences for generated tests.</param>
    /// <param name="generationBehavior">Scope and organization of generated tests.</param>
    /// <param name="aiConfiguration">Model and system prompt settings.</param>
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

    /// <summary>Gets or sets the configuration format version.</summary>
    [JsonProperty("version")] public string Version { get; set; }

    /// <summary>Gets or sets target framework and test project settings.</summary>
    [JsonProperty("environment")] public EnvironmentConfig Environment { get; set; }

    /// <summary>Gets or sets the test and mocking framework settings.</summary>
    [JsonProperty("frameworks")] public FrameworksConfig Frameworks { get; set; }

    /// <summary>Gets or sets generated-code formatting preferences.</summary>
    [JsonProperty("codeStyle")] public CodeStyleConfig CodeStyle { get; set; }

    /// <summary>Gets or sets test generation scope and organization settings.</summary>
    [JsonProperty("generationBehavior")] public GenerationBehaviorConfig GenerationBehavior { get; set; }

    /// <summary>Gets or sets the AI model and system prompt settings.</summary>
    [JsonProperty("aiConfiguration")] public AiConfigurationConfig AiConfiguration { get; set; }

}

/// <summary>Defines the target .NET framework and generated test project name.</summary>
public record EnvironmentConfig
{
    /// <summary>Creates environment settings.</summary>
    /// <param name="targetDotNetVersion">Target framework moniker for the generated test project.</param>
    /// <param name="testProjectName">Test project name; <c>{ProjectName}</c> is replaced with the source project name.</param>
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
    /// <summary>Creates framework settings.</summary>
    /// <param name="testFramework">Test framework identifier, such as <c>xunit</c> or <c>nunit</c>.</param>
    /// <param name="mockingFramework">Mocking framework identifier, such as <c>moq</c> or <c>nsubstitute</c>.</param>
    /// <param name="useFluentAssertions">Whether generated assertions should use FluentAssertions.</param>
    /// <param name="useAutoFixture">Whether generated tests should use AutoFixture for test data.</param>
    [JsonConstructor]
    public FrameworksConfig(string testFramework, string mockingFramework, bool useFluentAssertions, bool useAutoFixture)
    {
        TestFramework = testFramework;
        MockingFramework = mockingFramework;
        UseFluentAssertions = useFluentAssertions;
        UseAutoFixture = useAutoFixture;
    }

    /// <summary>Gets or sets the test framework identifier.</summary>
    public string TestFramework { get; set; }

    /// <summary>Gets or sets the mocking framework identifier.</summary>
    public string MockingFramework { get; set; }

    /// <summary>Gets or sets whether generated tests use FluentAssertions.</summary>
    public bool UseFluentAssertions { get; set; }

    /// <summary>Gets or sets whether generated tests use AutoFixture.</summary>
    public bool UseAutoFixture { get; set; }
}

/// <summary>Defines formatting preferences for generated test code.</summary>
public record CodeStyleConfig
{
    /// <summary>Creates code-style settings.</summary>
    /// <param name="useFileScopedNamespace">Whether to use file-scoped namespaces.</param>
    /// <param name="useAsyncSuffix">Whether asynchronous test methods should use the <c>Async</c> suffix.</param>
    /// <param name="maxLineLength">Preferred maximum line length.</param>
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
    /// <param name="testStrategy">Generation scope: <c>Unit</c>, <c>Integration</c>, or <c>Both</c>.</param>
    /// <param name="splitTestsByMethod">Whether to place tests for separate methods in separate files.</param>
    /// <param name="maxTestsPerClass">Maximum number of generated test methods per class.</param>
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

/// <summary>Defines the AI model, sampling temperature, and system prompt for generation.</summary>
public record AiConfigurationConfig
{
    /// <summary>Creates AI generation settings.</summary>
    /// <param name="model">Model identifier understood by the configured AI service.</param>
    /// <param name="temperature">Sampling temperature used to control response variability.</param>
    /// <param name="systemPrompt">System instructions supplied to the model.</param>
    [JsonConstructor]
    public AiConfigurationConfig(string model, double temperature, string systemPrompt)
    {
        Model = model;
        Temperature = temperature;
        SystemPrompt = systemPrompt;
    }

    /// <summary>Gets or sets the AI model identifier.</summary>
    public string Model { get; set; }

    /// <summary>Gets or sets the model sampling temperature.</summary>
    public double Temperature { get; set; }

    /// <summary>Gets or sets the system instructions supplied to the model.</summary>
    public string SystemPrompt { get; set; }
}