using NetAI.TestGenerator.Core.Models.Enums;

namespace NetAI.TestGenerator.Core.Extensions;

/// <summary>Single source of truth for framework-specific names, namespaces and attributes.</summary>
public static class TestFrameworkExtensions
{
    /// <summary>Gets the namespace that contains the framework's attributes and <c>Assert</c>.</summary>
    public static string Namespace(this TestFramework framework) => framework switch
    {
        TestFramework.NUnit => "NUnit.Framework",
        TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting",
        TestFramework.xUnit => "Xunit",
        _ => throw Unsupported(framework)
    };

    /// <summary>Gets the human-readable framework name (e.g. for prompts).</summary>
    public static string DisplayName(this TestFramework framework) => framework switch
    {
        TestFramework.NUnit => "NUnit",
        TestFramework.MSTest => "MSTest",
        TestFramework.xUnit => "xUnit",
        _ => throw Unsupported(framework)
    };

    /// <summary>Gets the <c>dotnet new</c> template short name.</summary>
    public static string TemplateName(this TestFramework framework) => framework switch
    {
        TestFramework.NUnit => "nunit",
        TestFramework.MSTest => "mstest",
        TestFramework.xUnit => "xunit",
        _ => throw Unsupported(framework)
    };

    /// <summary>Gets the attribute that marks a test method, e.g. <c>[Fact]</c>.</summary>
    public static string TestAttribute(this TestFramework framework) => framework switch
    {
        TestFramework.NUnit => "[Test]",
        TestFramework.MSTest => "[TestMethod]",
        TestFramework.xUnit => "[Fact]",
        _ => throw Unsupported(framework)
    };

    /// <summary>Gets the attribute required on test classes, or <c>null</c> if none is needed.</summary>
    public static string? ClassAttribute(this TestFramework framework) => framework == TestFramework.MSTest ? "[TestClass]" : null;

    /// <summary>
    /// Gets a skipped test attribute. NUnit/MSTest need the test attribute in addition to <c>[Ignore]</c>, otherwise the
    /// method is not discovered and never reported as skipped. <paramref name="reason"/> must already be escaped.
    /// </summary>
    public static string SkipAttribute(this TestFramework framework, string reason) => framework switch
    {
        TestFramework.NUnit => $"[Test, Ignore(\"{reason}\")]",
        TestFramework.MSTest => $"[TestMethod, Ignore(\"{reason}\")]",
        TestFramework.xUnit => $"[Fact(Skip = \"{reason}\")]",
        _ => throw Unsupported(framework)
    };

    private static ArgumentOutOfRangeException Unsupported(TestFramework framework) =>
        new(nameof(framework), framework, "Unsupported test framework.");
}

/// <summary>Framework-specific names for mocking libraries.</summary>
public static class MockFrameworkExtensions
{
    /// <summary>Gets the root namespace of the mocking library, or an empty string for <see cref="MockFramework.Unknown"/>.</summary>
    public static string Namespace(this MockFramework framework) => framework switch
    {
        MockFramework.Moq => "Moq",
        MockFramework.NSubstitute => "NSubstitute",
        MockFramework.FakeItEasy => "FakeItEasy",
        _ => string.Empty
    };
}
