using System.Collections.Generic;
using System.Linq;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models.Enums;

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>
///     Encapsulates all framework-specific decisions for the test skeleton and
///     the AI prompt: using directives, attributes, mock syntax, assertion style.
/// </summary>
public sealed class TestFrameworkProfile
{
    private readonly HashSet<string> _packages;

    public TestFrameworkProfile(
        TestFramework test,
        MockFramework mock,
        bool useFluentAssertions,
        bool useAutoFixture = false,
        IEnumerable<string>? availablePackages = null)
    {
        Test = test;
        Mock = mock;
        UseFluentAssertions = useFluentAssertions;
        UseAutoFixture = useAutoFixture;
        _packages = new HashSet<string>(
            availablePackages ?? Enumerable.Empty<string>(),
            System.StringComparer.OrdinalIgnoreCase);
    }

    public TestFramework Test { get; }
    public MockFramework Mock { get; }
    public bool UseFluentAssertions { get; }
    public bool UseAutoFixture { get; }

    public bool HasPackage(string id) => _packages.Contains(id);

    // ------------------------------------------------------------------ factories

    public static TestFrameworkProfile FromConfig(AiTestingConfig config, IEnumerable<string>? availablePackages = null)
    {
        var test = System.Enum.TryParse<TestFramework>(config.Frameworks?.TestFramework, true, out var t)
            ? t
            : TestFramework.Unknown;
        var mock = System.Enum.TryParse<MockFramework>(config.Frameworks?.MockingFramework, true, out var m)
            ? m
            : MockFramework.Unknown;

        return new TestFrameworkProfile(
            test,
            mock,
            config.Frameworks?.UseFluentAssertions ?? false,
            config.Frameworks?.UseAutoFixture ?? false,
            availablePackages);
    }

    public static TestFrameworkProfile Create(
        TestFramework test,
        MockFramework mock,
        bool useFluent = true,
        bool useAutoFixture = false,
        IEnumerable<string>? availablePackages = null)
        => new(test, mock, useFluent, useAutoFixture, availablePackages);

    // ------------------------------------------------------------------ test framework

    public string TestNamespace => Test switch
    {
        TestFramework.xUnit => "Xunit",
        TestFramework.NUnit => "NUnit.Framework",
        TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting",
        _ => ""
    };

    public string? ClassAttribute => Test switch
    {
        TestFramework.NUnit => "[TestFixture]",
        TestFramework.MSTest => "[TestClass]",
        _ => null
    };

    public bool UsesConstructorForSetup => Test == TestFramework.xUnit;

    public string? SetupAttribute => Test switch
    {
        TestFramework.NUnit => "[SetUp]",
        TestFramework.MSTest => "[TestInitialize]",
        _ => null
    };

    /// <summary>
    ///     Returns the fact attribute. When STA is required, the best available
    ///     variant is chosen automatically (StaFact when xUnit + package,
    ///     [Apartment(ApartmentState.STA)] for NUnit, [STATestMethod] for MSTest).
    /// </summary>
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

    /// <summary>
    ///     Plain skip attribute (no STA awareness). Used when STA is not required.
    /// </summary>
    public string SkipAttribute(string reason) => Test switch
    {
        TestFramework.xUnit => $"[Fact(Skip = \"{reason}\")]",
        TestFramework.NUnit => $"[Ignore(\"{reason}\")]",
        TestFramework.MSTest => $"[Ignore(\"{reason}\")]",
        _ => "[Fact(Skip = \"unsupported framework\")]"
    };

    /// <summary>
    ///     STA-aware skip attribute. For xUnit the StaFact variant is emitted when
    ///     the 'Xunit.StaFact' package is installed; otherwise a TODO comment is
    ///     appended so the reader knows how to make it STA-safe. For NUnit the
    ///     regular Ignore attribute is combined with [Apartment(ApartmentState.STA)]
    ///     (both come from the framework itself, no extra package needed). For MSTest
    ///     the plain Ignore attribute suffices because ignored tests never run.
    /// </summary>
    public string StaSkipAttribute(string reason) => Test switch
    {
        TestFramework.xUnit => HasPackage("Xunit.StaFact")
            ? $"[StaFact(Skip = \"{reason}\")]"
            : $"[Fact(Skip = \"{reason}\")] // TODO: STA required – add 'Xunit.StaFact' package and switch to [StaFact]",
        TestFramework.NUnit => $"[Ignore(\"{reason}\")] [Apartment(ApartmentState.STA)]",
        TestFramework.MSTest => $"[Ignore(\"{reason}\")]",
        _ => SkipAttribute(reason)
    };

    /// <summary>
    ///     Returns the appropriate skip attribute based on whether the test is
    ///     STA-bound. Single dispatch point for callers that only know the flag.
    /// </summary>
    public string SkipAttributeFor(string reason, bool requiresSta)
        => requiresSta ? StaSkipAttribute(reason) : SkipAttribute(reason);

    // ------------------------------------------------------------------ mock framework

    public bool HasMockFramework => Mock != MockFramework.Unknown;

    public string MockNamespace => Mock switch
    {
        MockFramework.Moq => "Moq",
        MockFramework.NSubstitute => "NSubstitute",
        MockFramework.FakeItEasy => "FakeItEasy",
        _ => ""
    };

    /// <summary>Field declaration. For Moq the field type is Mock&lt;T&gt;, otherwise T itself.</summary>
    public string FieldDeclaration(string typeName, string fieldName) => Mock switch
    {
        MockFramework.Moq => $"    private readonly Mock<{typeName}> {fieldName};",
        MockFramework.NSubstitute => $"    private readonly {typeName} {fieldName};",
        MockFramework.FakeItEasy => $"    private readonly {typeName} {fieldName};",
        _ => ""
    };

    public string FieldInitialization(string typeName, string fieldName) => Mock switch
    {
        MockFramework.Moq => $"        {fieldName} = new Mock<{typeName}>();",
        MockFramework.NSubstitute => $"        {fieldName} = Substitute.For<{typeName}>();",
        MockFramework.FakeItEasy => $"        {fieldName} = A.Fake<{typeName}>();",
        _ => ""
    };

    /// <summary>Value passed to the SUT constructor (Moq: .Object, otherwise the field itself).</summary>
    public string ConstructorArgument(string fieldName) => Mock switch
    {
        MockFramework.Moq => $"{fieldName}.Object",
        MockFramework.NSubstitute => fieldName,
        MockFramework.FakeItEasy => fieldName,
        _ => fieldName
    };

    /// <summary>Setup example snippet – used in the prompt shown to the LLM.</summary>
    public string SetupExample(string fieldName, string methodSignature) => Mock switch
    {
        MockFramework.Moq => $"{fieldName}.Setup(x => x.{methodSignature}).Returns(...);",
        MockFramework.NSubstitute => $"{fieldName}.{methodSignature}.Returns(...);",
        MockFramework.FakeItEasy => $"A.CallTo(() => {fieldName}.{methodSignature}).Returns(...);",
        _ => "// No mock framework configured – provide a concrete instance manually."
    };

    // ------------------------------------------------------------------ assertions

    public string AssertionExample => UseFluentAssertions
        ? "result.Should().Be(expected);"
        : Test switch
        {
            TestFramework.xUnit => "Assert.Equal(expected, result);",
            TestFramework.NUnit => "Assert.That(result, Is.EqualTo(expected));",
            TestFramework.MSTest => "Assert.AreEqual(expected, result);",
            _ => "// assert result"
        };

    // ------------------------------------------------------------------ usings

    /// <summary>
    ///     Namespaces the skeleton actually needs for the given purpose.
    ///     Skip-tests only declare a skip attribute, so they must not pull in
    ///     mock or assertion namespaces even when those are configured globally.
    /// </summary>
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

    /// <summary>
    ///     Convenience overload for full tests. Preserves the previous behavior
    ///     so existing call sites keep working unchanged.
    /// </summary>
    public IReadOnlyList<string> RequiredNamespaces()
        => RequiredNamespaces(SkeletonPurpose.FullTest);

    // ------------------------------------------------------------------ prompt summary

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

        var parts = new List<string>
        {
            $"Test framework: {test}",
            $"Mock framework: {mock}",
            $"Assertion style: {asserts}"
        };
        if (UseAutoFixture) parts.Add("AutoFixture is available");
        return string.Join(" | ", parts);
    }
}