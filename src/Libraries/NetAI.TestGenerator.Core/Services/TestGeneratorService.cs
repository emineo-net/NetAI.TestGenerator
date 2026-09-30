using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Reads existing test methods and creates or updates generated test class files.</summary>
public class TestGeneratorService
{
    /// <summary>Reads method names from an existing test file.</summary>
    /// <param name="testFilePath">Path to the test source file.</param>
    /// <returns>Names of all method declarations found, or an empty list when the file is missing or unreadable.</returns>
    public List<string> GetExistingTestMethods(string testFilePath)
    {
        var methodNames = new List<string>();
        if (!File.Exists(testFilePath)) return methodNames;

        try
        {
            string testCode = File.ReadAllText(testFilePath);
            var root = CSharpSyntaxTree.ParseText(testCode).GetCompilationUnitRoot();
            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var m in methods) methodNames.Add(m.Identifier.Text);
        }
        catch { }
        return methodNames;
    }

    /// <summary>Creates a formatted test class file containing the supplied generated method code.</summary>
    /// <param name="filePath">Destination path for the test file.</param>
    /// <param name="testClassName">Name of the generated test class.</param>
    /// <param name="originalNamespace">Source namespace used to derive the test namespace, when available.</param>
    /// <param name="methodCode">Test method code to place in the new class.</param>
    public void CreateNewTestClassFile(string filePath, string testClassName, NamespaceDeclarationSyntax? originalNamespace, string methodCode)
    {
        string namespaceName = originalNamespace?.Name.ToString() ?? "NetAI.Generated.Tests";
        if (!namespaceName.EndsWith(".Tests")) namespaceName += ".Tests";

        string fullCode = $@"using Xunit;
namespace {namespaceName}
{{
    public class {testClassName}
    {{
        {methodCode}
    }}
}}";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(fullCode);
        var formattedRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(tree.GetCompilationUnitRoot(), new AdhocWorkspace());

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, formattedRoot.ToFullString());
    }

    /// <summary>Appends the first method declaration found in the supplied code to an existing test class.</summary>
    /// <param name="filePath">Path to the existing test source file.</param>
    /// <param name="methodCode">Generated code containing the method to append.</param>
    public void AppendMethodToExistingClassFile(string filePath, string methodCode)
    {
        string existingCode = File.ReadAllText(filePath);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(existingCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl == null) return;

        var newMethodRoot = CSharpSyntaxTree.ParseText(methodCode).GetCompilationUnitRoot();
        var newMethodNode = newMethodRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        if (newMethodNode == null) return;

        var updatedClass = classDecl.AddMembers(newMethodNode);
        var newRoot = root.ReplaceNode(classDecl, updatedClass);

        var formattedRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(newRoot, new AdhocWorkspace());
        File.WriteAllText(filePath, formattedRoot.ToFullString());
    }
}
