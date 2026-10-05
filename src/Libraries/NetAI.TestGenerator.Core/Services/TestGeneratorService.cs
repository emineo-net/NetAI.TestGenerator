using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>
///     Reads existing test methods and creates or updates generated test class files.
///     All members of an incoming class snippet are preserved verbatim — fields,
///     constructors, properties, and methods — so that mock fields and the SUT
///     constructor survive into the final written file.
/// </summary>
public class TestGeneratorService
{
    // ------------------------------------------------------------------ read

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
            // Unreadable file — treat as no existing tests.
        }

        return methodNames;
    }

    // ------------------------------------------------------------------ create

    /// <summary>
    ///     Creates a formatted test class file from a class snippet. All members of
    ///     the snippet's test class (fields, constructor, helper methods, and test
    ///     methods) are preserved in their original order. Using directives are
    ///     hoisted to the top of the file and de-duplicated.
    /// </summary>
    /// <param name="filePath">Destination path for the test file.</param>
    /// <param name="testClassName">Name of the generated test class.</param>
    /// <param name="testNamespaceName">Namespace for the generated test class.</param>
    /// <param name="methodCode">
    ///     Complete class snippet, typically produced by <c>ExtractTestClass</c>.
    ///     May contain compilation-unit-level using directives; they are hoisted.
    /// </param>
    /// <param name="frameworkUsing">
    ///     Fully-qualified test-framework using directive (e.g. <c>using Xunit;</c>).
    ///     Prepended to the using block when non-null.
    /// </param>
    public void CreateNewTestClassFile(string filePath, string testClassName, string? testNamespaceName,
        string methodCode, string? frameworkUsing = null)
    {
        var namespaceName = string.IsNullOrWhiteSpace(testNamespaceName)
            ? "NetAI.Generated.Tests"
            : testNamespaceName!;

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

    // ------------------------------------------------------------------ append

    /// <summary>
    ///     Appends all members of the supplied class snippet to an existing test
    ///     class. Duplicate members are detected by key: methods by signature,
    ///     fields by variable names, constructors by parameter list. New using
    ///     directives from the snippet are merged into the file's using block.
    /// </summary>
    /// <param name="filePath">Path to the existing test source file.</param>
    /// <param name="methodCode">Complete class snippet with the members to append.</param>
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

        // Which members does the existing class already have?
        var existingKeys = new HashSet<string>(
            classDecl.Members.Select(GetMemberKey),
            StringComparer.Ordinal);

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

        // Merge usings.
        var usings = root.Usings.ToList();
        var existingUsingKeys = new HashSet<string>(usings.Select(GetUsingKey), StringComparer.Ordinal);

        foreach (var usingText in extractedUsings)
        {
            var parsedUsing = CSharpSyntaxTree.ParseText(usingText)
                .GetCompilationUnitRoot()
                .Usings.FirstOrDefault();

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

    // ------------------------------------------------------------------ class snippet parsing

    /// <summary>
    ///     Splits a class snippet into top-level using directives and the members
    ///     of its test class. When the snippet contains a full class, all of its
    ///     members are returned in order (fields, constructor, methods). When it
    ///     contains only method declarations, those are returned as-is.
    /// </summary>
    private static (List<string> Usings, List<MemberDeclarationSyntax> Members) SplitUsingsAndMembers(string code)
    {
        var usings = new List<string>();
        var members = new List<MemberDeclarationSyntax>();

        if (string.IsNullOrWhiteSpace(code))
        {
            return (usings, members);
        }

        var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();

        // Collect top-level using directives.
        foreach (var u in root.Usings)
        {
            var text = u.ToFullString().TrimEnd();
            if (!usings.Contains(text, StringComparer.Ordinal))
            {
                usings.Add(text);
            }
        }

        // Preferred path: the snippet contains a full class.
        var classDecl = FindTestClass(root);
        if (classDecl is not null)
        {
            members.AddRange(classDecl.Members);
            return (usings, members);
        }

        // Fallback: methods-only snippet.
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        if (methods.Count == 0)
        {
            var classMembers = code;
            foreach (var u in root.Usings.OrderByDescending(x => x.SpanStart))
            {
                classMembers = classMembers.Remove(u.SpanStart, u.Span.Length);
            }

            var wrapped = CSharpSyntaxTree.ParseText($"class GeneratedTestContainer {{ {classMembers} }}")
                .GetCompilationUnitRoot();
            methods = wrapped.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        }

        members.AddRange(methods);
        return (usings, members);
    }

    // ------------------------------------------------------------------ compilation-unit builder

    private static CompilationUnitSyntax BuildCompilationUnit(
        string namespaceName,
        string testClassName,
        IEnumerable<string> usings,
        IReadOnlyList<MemberDeclarationSyntax> members)
    {
        var classDecl = SyntaxFactory.ClassDeclaration(testClassName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(members.ToArray());

        var namespaceDecl = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName))
            .AddMembers(classDecl);

        var usingNodes = usings
            .Select(u => CSharpSyntaxTree.ParseText(u).GetCompilationUnitRoot().Usings.FirstOrDefault())
            .Where(u => u is not null)
            .Cast<UsingDirectiveSyntax>()
            .ToArray();

        return SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usingNodes))
            .AddMembers(namespaceDecl);
    }

    // ------------------------------------------------------------------ class lookup

    /// <summary>
    ///     Finds the test class in a compilation unit. Prefers the class that
    ///     carries at least one method with a recognized test attribute; falls back
    ///     to the first class declaration when no such class exists.
    /// </summary>
    private static ClassDeclarationSyntax? FindTestClass(CompilationUnitSyntax root)
    {
        var byAttribute = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(HasTestAttribute));

        return byAttribute
               ?? root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
    }

    // ------------------------------------------------------------------ member keys (for dedup)

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
        var parts = new List<string>
        {
            method.Identifier.Text,
            (method.TypeParameterList?.Parameters.Count ?? 0).ToString()
        };

        foreach (var p in method.ParameterList.Parameters)
        {
            var refKind = string.Empty;
            foreach (var modifier in p.Modifiers)
            {
                if (modifier.IsKind(SyntaxKind.RefKeyword)
                    || modifier.IsKind(SyntaxKind.OutKeyword)
                    || modifier.IsKind(SyntaxKind.InKeyword))
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
        return string.Join("|",
            normalized.GlobalKeyword.RawKind,
            normalized.StaticKeyword.RawKind,
            normalized.Alias?.Name.ToString(),
            normalized.Name?.ToString());
    }

    // ------------------------------------------------------------------ test attribute detection

    private static bool HasTestAttribute(MethodDeclarationSyntax method)
    {
        foreach (var list in method.AttributeLists)
        {
            foreach (var attr in list.Attributes)
            {
                var name = attr.Name.ToString();

                if (name.EndsWith("Fact", StringComparison.Ordinal)
                    || name.EndsWith("Theory", StringComparison.Ordinal)
                    || name.EndsWith("Test", StringComparison.Ordinal)
                    || name.EndsWith("TestCase", StringComparison.Ordinal)
                    || name.EndsWith("TestMethod", StringComparison.Ordinal)
                    || name.EndsWith("DataTestMethod", StringComparison.Ordinal)
                    || name.EndsWith("StaFact", StringComparison.Ordinal)
                    || name.EndsWith("STATestMethod", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}