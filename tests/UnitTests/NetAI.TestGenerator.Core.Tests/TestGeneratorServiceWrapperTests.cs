using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Services;
using System.Reflection;
using Xunit;

namespace NetAI.TestGenerator.Core.Tests;

public class TestGeneratorServiceWrapperTests
{
    [Fact]
    public void PrepareValidationStructure_DoesNotNestIncomingNamespaceOrClass()
    {
        var orchestrator = new ResxTranslationOrchestrator();
        var prepareMethod = typeof(ResxTranslationOrchestrator).GetMethod(
            "PrepareValidationStructure",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(prepareMethod);
        string validationCode = (string)prepareMethod.Invoke(orchestrator, new object?[]
        {
            "MainWindowTests",
            null,
            """
            using Xunit;
            namespace NetAI.Generated.Tests
            {
                public class MainWindowTests
                {
                    [Fact]
                    public void TestButton_OnClick_ShouldBeSkipped() { }
                }
            }
            """
        })!;

        var root = CSharpSyntaxTree.ParseText(validationCode).GetCompilationUnitRoot();
        var namespaces = root.Members.OfType<NamespaceDeclarationSyntax>().ToArray();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToArray();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();

        Assert.Single(namespaces);
        Assert.Single(classes, declaration => declaration.Identifier.ValueText == "MainWindowTests");
        Assert.Single(methods, declaration =>
            declaration.Identifier.ValueText == "TestButton_OnClick_ShouldBeSkipped");
        Assert.DoesNotContain(root.DescendantNodes().OfType<UsingDirectiveSyntax>(),
            usingDirective => !root.Usings.Contains(usingDirective));
        Assert.DoesNotContain(root.GetDiagnostics(),
            diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void CreateNewTestClassFile_FlattensCompleteGeneratedTestClass()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(testDirectory, "MainWindowTests.cs");

        try
        {
            new TestGeneratorService().CreateNewTestClassFile(
                filePath,
                "MainWindowTests",
                originalNamespace: null,
                """
                using Xunit;

                namespace NetAI.Generated.Tests
                {
                    public class MainWindowTests
                    {
                        [Fact]
                        public void TestButton_OnClick_ShouldBeSkipped() { }
                    }
                }
                """);

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)).GetCompilationUnitRoot();

            Assert.Single(root.Members.OfType<NamespaceDeclarationSyntax>());
            Assert.Single(root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                classDeclaration => classDeclaration.Identifier.ValueText == "MainWindowTests");
            Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.ValueText == "TestButton_OnClick_ShouldBeSkipped");
            Assert.DoesNotContain(root.DescendantNodes().OfType<UsingDirectiveSyntax>(),
                usingDirective => !root.Usings.Contains(usingDirective));
            Assert.DoesNotContain(root.GetDiagnostics(),
                diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreateNewTestClassFile_RejectsWrappedCodeWithoutTestMethod()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(testDirectory, "MainWindowTests.cs");

        try
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                new TestGeneratorService().CreateNewTestClassFile(
                    filePath,
                    "MainWindowTests",
                    originalNamespace: null,
                    """
                    using Xunit;

                    namespace NetAI.Generated.Tests
                    {
                        public class MainWindowTests
                        {
                            [Fact(Skip = "No executable method was generated.")]
                            // public void TestButton_OnClick_ShouldBeSkipped() { }
                        }
                    }
                    """));

            Assert.Contains("does not contain a test method", exception.Message);
            Assert.False(File.Exists(filePath));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void ExtractTestClass_FlattensCompleteGeneratedTestClass()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(testDirectory, "MainWindowTests.cs");
        string extracted = ResxTranslationOrchestrator.ExtractTestClass(
            """
            using Xunit;

            namespace NetAI.Generated.Tests
            {
                public class MainWindowTests
                {
                    [Fact]
                    public void TestButton_OnClick_ShouldBeSkipped() { }
                }
            }
            """);

        try
        {
            Assert.Contains("TestButton_OnClick_ShouldBeSkipped", extracted);
            Assert.DoesNotContain("namespace NetAI.Generated.Tests", extracted);
            Assert.DoesNotContain("class MainWindowTests", extracted);

            new TestGeneratorService().CreateNewTestClassFile(
                filePath,
                "MainWindowTests",
                originalNamespace: null,
                extracted);

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)).GetCompilationUnitRoot();
            Assert.Single(root.Members.OfType<NamespaceDeclarationSyntax>());
            Assert.Single(root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                classDeclaration => classDeclaration.Identifier.ValueText == "MainWindowTests");
            Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.ValueText == "TestButton_OnClick_ShouldBeSkipped");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }
}
