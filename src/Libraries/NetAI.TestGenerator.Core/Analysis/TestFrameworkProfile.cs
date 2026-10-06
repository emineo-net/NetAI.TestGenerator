using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models.Enums;

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>Stores framework settings used to generate test skeletons and prompts.</summary>
public sealed class TestFrameworkProfile
{
    private readonly HashSet<string> _packages;

    /// <summary>Initializes a test framework profile instance.</summary>
    public TestFrameworkProfile(TestFramework test, MockFramework mock, bool useFluentAssertions, bool useAutoFixture = false,
        IEnumerable<string>? availablePackages = null)
    {
        Test = test;
        Mock = mock;
        UseFluentAssertions = useFluentAssertions;
        UseAutoFixture = useAutoFixture;
        _packages = new HashSet<string>(availablePackages ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the selected test framework.</summary>
    public TestFramework Test { get; }

    /// <summary>Gets the selected mocking framework.</summary>
    public MockFramework Mock { get; }

    /// <summary>Gets whether Fluent Assertions is enabled.</summary>
    public bool UseFluentAssertions { get; }

    /// <summary>Gets whether AutoFixture is enabled.</summary>
    public bool UseAutoFixture { get; }

    /// <summary>Gets the test namespace.</summary>
    public string TestNamespace =>
        Test switch
        {
            TestFramework.xUnit => "Xunit",
            TestFramework.NUnit => "NUnit.Framework",
            TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting",
            _ => ""
        };

    /// <summary>Gets the class attribute.</summary>
    public string? ClassAttribute =>
        Test switch
        {
            TestFramework.NUnit => "[TestFixture]",
            TestFramework.MSTest => "[TestClass]",
            _ => null
        };

    /// <summary>Gets whether the framework uses a constructor for setup.</summary>
    public bool UsesConstructorForSetup => Test == TestFramework.xUnit;

    /// <summary>Gets the setup attribute.</summary>
    public string? SetupAttribute =>
        Test switch
        {
            TestFramework.NUnit => "[SetUp]",
            TestFramework.MSTest => "[TestInitialize]",
            _ => null
        };

    /// <summary>Gets whether a mocking framework is configured.</summary>
    public bool HasMockFramework => Mock != MockFramework.Unknown;

    /// <summary>Gets the mock namespace.</summary>
    public string MockNamespace =>
        Mock switch
        {
            MockFramework.Moq => "Moq",
            MockFramework.NSubstitute => "NSubstitute",
            MockFramework.FakeItEasy => "FakeItEasy",
            _ => ""
        };

    /// <summary>Gets the assertion example.</summary>
    public string AssertionExample =>
        UseFluentAssertions
            ? "result.Should().Be(expected);"
            : Test switch
            {
                TestFramework.xUnit => "Assert.Equal(expected, result);",
                TestFramework.NUnit => "Assert.That(result, Is.EqualTo(expected));",
                TestFramework.MSTest => "Assert.AreEqual(expected, result);",
                _ => "// assert result"
            };

    /// <summary>Checks whether package is available.</summary>
    public bool HasPackage(string id)
    {
        return _packages.Contains(id);
    }

    /// <summary>Creates a profile from the test configuration.</summary>
    public static TestFrameworkProfile FromConfig(AiTestingConfig config, IEnumerable<string>? availablePackages = null)
    {
        var test = Enum.TryParse<TestFramework>(config.Frameworks?.TestFramework, true, out var t) ? t : TestFramework.Unknown;
        var mock = Enum.TryParse<MockFramework>(config.Frameworks?.MockingFramework, true, out var m) ? m : MockFramework.Unknown;

        return new TestFrameworkProfile(test, mock, config.Frameworks?.UseFluentAssertions ?? false,
            config.Frameworks?.UseAutoFixture ?? false, availablePackages);
    }

    /// <summary>Creates a profile from explicit framework settings.</summary>
    public static TestFrameworkProfile Create(TestFramework test, MockFramework mock, bool useFluent = true, bool useAutoFixture = false,
        IEnumerable<string>? availablePackages = null)
    {
        return new TestFrameworkProfile(test, mock, useFluent, useAutoFixture, availablePackages);
    }

    /// <summary>Returns the fact attribute.</summary>
    public string FactAttribute(bool requiresSta)
    {
        if (!requiresSta)
        {
            return Test switch
            {
                TestFramework.xUnit => "[Fact]",
                TestFramework.NUnit => "[Test]",
                TestFramework.MSTest => "[TestMethod]",
                _ => "[Fact]"
            };
        }

        return Test switch
        {
            TestFramework.xUnit => HasPackage("Xunit.StaFact")
                ? "[StaFact]"
                : "[Fact] // TODO: STA required – add 'Xunit.StaFact' package and switch to [StaFact]",
            TestFramework.NUnit => "[Test] [Apartment(ApartmentState.STA)]",
            TestFramework.MSTest => "[STATestMethod]",
            _ => "[Fact]"
        };
    }

    /// <summary>Plain skip attribute (no STA awareness). Used when STA is not required.</summary>
    public string SkipAttribute(string reason)
    {
        return Test switch
        {
            TestFramework.xUnit => $"[Fact(Skip = \"{reason}\")]",
            TestFramework.NUnit => $"[Ignore(\"{reason}\")]",
            TestFramework.MSTest => $"[Ignore(\"{reason}\")]",
            _ => "[Fact(Skip = \"unsupported framework\")]"
        };
    }

    /// <summary>STA-aware skip attribute.</summary>
    public string StaSkipAttribute(string reason)
    {
        return Test switch
        {
            TestFramework.xUnit => HasPackage("Xunit.StaFact")
                ? $"[StaFact(Skip = \"{reason}\")]"
                : $"[Fact(Skip = \"{reason}\")] // TODO: STA required – add 'Xunit.StaFact' package and switch to [StaFact]",
            TestFramework.NUnit => $"[Ignore(\"{reason}\")] [Apartment(ApartmentState.STA)]",
            TestFramework.MSTest => $"[Ignore(\"{reason}\")]",
            _ => SkipAttribute(reason)
        };
    }

    /// <summary>Returns the appropriate skip attribute based on whether the test is STA-bound.</summary>
    public string SkipAttributeFor(string reason, bool requiresSta)
    {
        return requiresSta ? StaSkipAttribute(reason) : SkipAttribute(reason);
    }

    /// <summary>Field declaration. For Moq the field type is Mock&lt;T&gt;, otherwise T itself.</summary>
    public string FieldDeclaration(string typeName, string fieldName)
    {
        return Mock switch
        {
            MockFramework.Moq => $"    private readonly Mock<{typeName}> {fieldName};",
            MockFramework.NSubstitute => $"    private readonly {typeName} {fieldName};",
            MockFramework.FakeItEasy => $"    private readonly {typeName} {fieldName};",
            _ => ""
        };
    }

    /// <summary>Formats initialization code for the configured mock framework.</summary>
    public string FieldInitialization(string typeName, string fieldName)
    {
        return Mock switch
        {
            MockFramework.Moq => $"        {fieldName} = new Mock<{typeName}>();",
            MockFramework.NSubstitute => $"        {fieldName} = Substitute.For<{typeName}>();",
            MockFramework.FakeItEasy => $"        {fieldName} = A.Fake<{typeName}>();",
            _ => ""
        };
    }

    /// <summary>Value passed to the SUT constructor (Moq: .Object, otherwise the field itself).</summary>
    public string ConstructorArgument(string fieldName)
    {
        return Mock switch
        {
            MockFramework.Moq => $"{fieldName}.Object",
            MockFramework.NSubstitute => fieldName,
            MockFramework.FakeItEasy => fieldName,
            _ => fieldName
        };
    }

    /// <summary>Setup example snippet – used in the prompt shown to the LLM.</summary>
    public string SetupExample(string fieldName, string methodSignature)
    {
        return Mock switch
        {
            MockFramework.Moq => $"{fieldName}.Setup(x => x.{methodSignature}).Returns(...);",
            MockFramework.NSubstitute => $"{fieldName}.{methodSignature}.Returns(...);",
            MockFramework.FakeItEasy => $"A.CallTo(() => {fieldName}.{methodSignature}).Returns(...);",
            _ => "// No mock framework configured – provide a concrete instance manually."
        };
    }

    /// <summary>Namespaces the skeleton actually needs for the given purpose.</summary>
    public IReadOnlyList<string> RequiredNamespaces(SkeletonPurpose purpose)
    {
        var list = new List<string>();

        if (!string.IsNullOrEmpty(TestNamespace))
        {
            list.Add(TestNamespace);
        }

        if (purpose == SkeletonPurpose.FullTest)
        {
            if (HasMockFramework && !string.IsNullOrEmpty(MockNamespace))
            {
                list.Add(MockNamespace);
            }

            if (UseFluentAssertions)
            {
                list.Add("FluentAssertions");
            }
        }

        return list;
    }

    /// <summary>Gets the namespaces required for the selected skeleton purpose.</summary>
    public IReadOnlyList<string> RequiredNamespaces()
    {
        return RequiredNamespaces(SkeletonPurpose.FullTest);
    }

    /// <summary>Short text fragment used at the top of the AI prompt.</summary>
    public string ToPromptHeader()
    {
        var test = Test switch
        {
            TestFramework.xUnit => "xUnit",
            TestFramework.NUnit => "NUnit",
            TestFramework.MSTest => "MSTest",
            _ => Test.ToString()
        };
        var mock = HasMockFramework ? Mock.ToString() : "none";
        var asserts = UseFluentAssertions ? "FluentAssertions" : test + " built-in asserts";

        var parts = new List<string> { $"Test framework: {test}", $"Mock framework: {mock}", $"Assertion style: {asserts}" };
        if (UseAutoFixture)
        {
            parts.Add("AutoFixture is available");
        }

        return string.Join(" | ", parts);
    }
}