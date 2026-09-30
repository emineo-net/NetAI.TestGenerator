using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetAI.TestGenerator.Core.Services;

public class TestGeneratorService
{
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
