using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Reads existing test methods and creates or updates generated test class files.</summary>
public class TestGeneratorService
{
    /// <summary>Reads method names from an existing test file.</summary>
    public List<string> GetExistingTestMethods(string testFilePath)
    {
        var methodNames = new List<string>();
        if (!File.Exists(testFilePath))
        {
            return methodNames;
        }

        try
        {
            var testCode = File.ReadAllText(testFilePath);
            var root = CSharpSyntaxTree.ParseText(testCode).GetCompilationUnitRoot();
            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var m in methods)
            {
                methodNames.Add(m.Identifier.Text);
            }
        }
        catch
        {
        }

        return methodNames;
    }

    /// <summary>Creates a formatted test class file from a class snippet.</summary>
    public void CreateNewTestClassFile(string filePath, string testClassName, string? testNamespaceName, string methodCode,
        string? frameworkUsing = null)
    {
        var namespaceName = string.IsNullOrWhiteSpace(testNamespaceName) ? "NetAI.Generated.Tests" : testNamespaceName!;

        if (!namespaceName.EndsWith(".Tests", StringComparison.Ordinal))
        {
            namespaceName += ".Tests";
        }

        var (extractedUsings, members) = SplitUsingsAndMembers(methodCode);
        if (members.Count == 0)
        {
            throw new ArgumentException("Generated code does not contain any class members.", nameof(methodCode));
        }

        var usings = new List<string>();

        if (frameworkUsing is { } configuredFrameworkUsing && !string.IsNullOrWhiteSpace(configuredFrameworkUsing))
        {
            usings.Add(configuredFrameworkUsing.TrimEnd());
        }

        foreach (var u in extractedUsings)
        {
            if (!usings.Contains(u, StringComparer.Ordinal))
            {
                usings.Add(u);
            }
        }

        var compilationUnit = BuildCompilationUnit(namespaceName, testClassName, usings, members);
        var formattedRoot = Formatter.Format(compilationUnit, new AdhocWorkspace());

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, formattedRoot.ToFullString());
    }

    /// <summary>Appends all members of the supplied class snippet to an existing test class.</summary>
    public void AppendMethodToExistingClassFile(string filePath, string methodCode)
    {
        var existingCode = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(existingCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var classDecl = FindTestClass(root);
        if (classDecl == null)
        {
            return;
        }

        var (extractedUsings, incomingMembers) = SplitUsingsAndMembers(methodCode);
        if (incomingMembers.Count == 0)
        {
            throw new ArgumentException("Generated code does not contain any class members.", nameof(methodCode));
        }

        var existingKeys = new HashSet<string>(classDecl.Members.Select(GetMemberKey), StringComparer.Ordinal);

        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var member in incomingMembers)
        {
            var key = GetMemberKey(member);
            if (existingKeys.Add(key))
            {
                newMembers.Add(member);
            }
        }

        if (newMembers.Count == 0)
        {
            return;
        }

        var usings = root.Usings.ToList();
        var existingUsingKeys = new HashSet<string>(usings.Select(GetUsingKey), StringComparer.Ordinal);

        foreach (var usingText in extractedUsings)
        {
            var parsedUsing = CSharpSyntaxTree.ParseText(usingText).GetCompilationUnitRoot().Usings.FirstOrDefault();

            if (parsedUsing != null && existingUsingKeys.Add(GetUsingKey(parsedUsing)))
            {
                usings.Add(parsedUsing);
            }
        }

        var updatedClass = classDecl.AddMembers(newMembers.ToArray());
        var newRoot = root.ReplaceNode(classDecl, updatedClass).WithUsings(SyntaxFactory.List(usings));

        var formattedRoot = Formatter.Format(newRoot, new AdhocWorkspace());
        File.WriteAllText(filePath, formattedRoot.ToFullString());
    }

    private static (List<string> Usings, List<MemberDeclarationSyntax> Members) SplitUsingsAndMembers(string code)
    {
        var usings = new List<string>();
        var members = new List<MemberDeclarationSyntax>();

        if (string.IsNullOrWhiteSpace(code))
        {
            return (usings, members);
        }

        var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();

        foreach (var u in root.Usings)
        {
            var text = u.ToFullString().TrimEnd();
            if (!usings.Contains(text, StringComparer.Ordinal))
            {
                usings.Add(text);
            }
        }

        var classDecl = FindTestClass(root);
        if (classDecl is not null)
        {
            members.AddRange(classDecl.Members);
            return (usings, members);
        }

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        if (methods.Count == 0)
        {
            var classMembers = code;
            foreach (var u in root.Usings.OrderByDescending(x => x.SpanStart))
            {
                classMembers = classMembers.Remove(u.SpanStart, u.Span.Length);
            }

            var wrapped = CSharpSyntaxTree.ParseText($"class GeneratedTestContainer {{ {classMembers} }}").GetCompilationUnitRoot();
            methods = wrapped.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        }

        members.AddRange(methods);
        return (usings, members);
    }

    private static CompilationUnitSyntax BuildCompilationUnit(string namespaceName, string testClassName, IEnumerable<string> usings,
        IReadOnlyList<MemberDeclarationSyntax> members)
    {
        var classDecl = SyntaxFactory.ClassDeclaration(testClassName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(members.ToArray());

        var namespaceDecl = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName)).AddMembers(classDecl);

        var usingNodes = usings.Select(u => CSharpSyntaxTree.ParseText(u).GetCompilationUnitRoot().Usings.FirstOrDefault())
            .Where(u => u is not null).Cast<UsingDirectiveSyntax>().ToArray();

        return SyntaxFactory.CompilationUnit().WithUsings(SyntaxFactory.List(usingNodes)).AddMembers(namespaceDecl);
    }

    private static ClassDeclarationSyntax? FindTestClass(CompilationUnitSyntax root)
    {
        var byAttribute = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(HasTestAttribute));

        return byAttribute ?? root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
    }

    private static string GetMemberKey(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax m:
                return "M:" + GetMethodKey(m);

            case ConstructorDeclarationSyntax c:
                return "C:" + string.Join(",", c.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""));

            case FieldDeclarationSyntax f:
                return "F:" + string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text));

            case PropertyDeclarationSyntax p:
                return "P:" + p.Identifier.Text;

            case EventFieldDeclarationSyntax e:
                return "E:" + string.Join(",", e.Declaration.Variables.Select(v => v.Identifier.Text));

            default:
                return "O:" + member.NormalizeWhitespace().ToFullString();
        }
    }

    private static string GetMethodKey(MethodDeclarationSyntax method)
    {
        var parts = new List<string> { method.Identifier.Text, (method.TypeParameterList?.Parameters.Count ?? 0).ToString() };

        foreach (var p in method.ParameterList.Parameters)
        {
            var refKind = string.Empty;
            foreach (var modifier in p.Modifiers)
            {
                if (modifier.IsKind(SyntaxKind.RefKeyword) || modifier.IsKind(SyntaxKind.OutKeyword) ||
                    modifier.IsKind(SyntaxKind.InKeyword))
                {
                    refKind = modifier.ValueText;
                    break;
                }
            }

            parts.Add((p.Type?.WithoutTrivia().ToString() ?? string.Empty) + ":" + refKind);
        }

        return string.Join("|", parts);
    }

    private static string GetUsingKey(UsingDirectiveSyntax usingDirective)
    {
        var normalized = usingDirective.WithoutTrivia();
        return string.Join("|", normalized.GlobalKeyword.RawKind, normalized.StaticKeyword.RawKind, normalized.Alias?.Name.ToString(),
            normalized.Name?.ToString());
    }

    private static bool HasTestAttribute(MethodDeclarationSyntax method)
    {
        foreach (var list in method.AttributeLists)
        {
            foreach (var attr in list.Attributes)
            {
                var name = attr.Name.ToString();

                if (name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal) ||
                    name.EndsWith("Test", StringComparison.Ordinal) || name.EndsWith("TestCase", StringComparison.Ordinal) ||
                    name.EndsWith("TestMethod", StringComparison.Ordinal) || name.EndsWith("DataTestMethod", StringComparison.Ordinal) ||
                    name.EndsWith("StaFact", StringComparison.Ordinal) || name.EndsWith("STATestMethod", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}