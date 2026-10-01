using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core.Services;
using Xunit;

namespace NetAI.TestGenerator.Core.Tests;

public class TestGeneratorServiceDuplicateMethodTests
{
    [Fact]
    public void CreateNewTestClassFile_WritesDuplicateMethodSignatureOnlyOnce()
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
                [Fact]
                public void GeneratedTest() { }

                [Fact]
                public void GeneratedTest() { }
                """);

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)).GetCompilationUnitRoot();

            Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.ValueText == "GeneratedTest");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void AppendMethodToExistingClassFile_DoesNotAppendExistingMethodSignature()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(testDirectory, "GeneratedTests.cs");
        Directory.CreateDirectory(testDirectory);

        try
        {
            File.WriteAllText(filePath, """
                using Xunit;

                public class GeneratedTests
                {
                    [Fact]
                    public void GeneratedTest() { }
                }
                """);

            new TestGeneratorService().AppendMethodToExistingClassFile(
                filePath,
                """
                [Fact]
                public void GeneratedTest() { }
                """);

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)).GetCompilationUnitRoot();

            Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.ValueText == "GeneratedTest");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }
}
