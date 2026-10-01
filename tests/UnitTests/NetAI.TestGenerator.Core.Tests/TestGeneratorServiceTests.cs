using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core.Services;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace NetAI.TestGenerator.Core.Tests;

public class TestGeneratorServiceTests
{
    [Fact]
    public void CreateNewTestClassFile_MovesGeneratedUsingsOutsideClass()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(testDirectory, "GeneratedTests.cs");

        try
        {
            new TestGeneratorService().CreateNewTestClassFile(
                filePath,
                "GeneratedTests",
                originalNamespace: null,
                """
                using Xunit;

                [Fact]
                public void GeneratedTest() { }
                """);

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)).GetCompilationUnitRoot();

            Assert.Single(root.Usings.Where(usingDirective =>
                usingDirective.Name?.ToString() == "Xunit"));
            Assert.Empty(root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(usingDirective => !root.Usings.Contains(usingDirective)));
            Assert.DoesNotContain(root.GetDiagnostics(), diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            Assert.Contains(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.Text == "GeneratedTest");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void AppendMethodToExistingClassFile_MovesGeneratedUsingsOutsideClass()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(testDirectory, "GeneratedTests.cs");
        Directory.CreateDirectory(testDirectory);

        try
        {
            File.WriteAllText(filePath, """
                using Xunit;

                namespace NetAI.Generated.Tests
                {
                    public class GeneratedTests
                    {
                    }
                }
                """);

            new TestGeneratorService().AppendMethodToExistingClassFile(
                filePath,
                """
                using Xunit;

                [Fact]
                public void GeneratedTest() { }
                """);

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)).GetCompilationUnitRoot();

            Assert.Single(root.Usings.Where(usingDirective =>
                usingDirective.Name?.ToString() == "Xunit"));
            Assert.Empty(root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(usingDirective => !root.Usings.Contains(usingDirective)));
            Assert.DoesNotContain(root.GetDiagnostics(), diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            Assert.Contains(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.Text == "GeneratedTest");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }
}
