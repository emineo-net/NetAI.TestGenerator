using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

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

    /// <summary>Creates a formatted test class file containing the supplied generated method code.</summary>
    /// <param name="filePath">Destination path for the test file.</param>
    /// <param name="testClassName">Name of the generated test class.</param>
    /// <param name="originalNamespace">Source namespace used to derive the test namespace, when available.</param>
    /// <param name="methodCode">
    ///     Test method code to place in the new class. May contain compilation-unit level
    ///     using directives (e.g. from <c>ExtractTestClass</c>); these are hoisted to the
    ///     top of the file, not embedded in the class body.
    /// </param>
    /// <param name="frameworkUsing">
    ///     Fully-qualified test-framework using directive (e.g. <c>using Xunit;</c>,
    ///     <c>using NUnit.Framework;</c>, <c>using Microsoft.VisualStudio.TestTools.UnitTesting;</c>).
    ///     Supplied by the caller because the orchestrator owns the selected framework.
    ///     When <see langword="null" /> or whitespace, no framework using is prepended; the
    ///     snippet's own using directives are used as-is.
    /// </param>
    public void CreateNewTestClassFile(string filePath, string testClassName, NamespaceDeclarationSyntax? originalNamespace,
        string methodCode, string? frameworkUsing = null)
    {
        var namespaceName = originalNamespace?.Name.ToString() ?? "NetAI.Generated.Tests";
        if (!namespaceName.EndsWith(".Tests", StringComparison.Ordinal))
        {
            namespaceName += ".Tests";
        }

        // usings vom Methodenkörper trennen, sonst landen sie im Klassenkörper (CS1529).
        var (extractedUsings, methodsText) = SplitUsingsFromMethods(methodCode);
        if (string.IsNullOrWhiteSpace(methodsText))
        {
            throw new ArgumentException("Generated code does not contain a test method.", nameof(methodCode));
        }

        var usings = new List<string>();

        // Framework-Using nur voranstellen, wenn der Aufrufer eines mitgegeben hat.
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

        // Sonderfall: keine usings, dann auch keine leere Zeile vor "namespace".
        var usingsBlock = usings.Count > 0 ? string.Join(Environment.NewLine, usings) + Environment.NewLine : string.Empty;

        var fullCode = $@"{usingsBlock}namespace {namespaceName}
{{
    public class {testClassName}
    {{
        {methodsText}
    }}
}}";
        var tree = CSharpSyntaxTree.ParseText(fullCode);
        var formattedRoot = Formatter.Format(tree.GetCompilationUnitRoot(), new AdhocWorkspace());

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, formattedRoot.ToFullString());
    }

    /// <summary>Appends the first method declaration found in the supplied code to an existing test class.</summary>
    /// <param name="filePath">Path to the existing test source file.</param>
    /// <param name="methodCode">
    ///     Generated code containing the method to append. May contain compilation-unit level
    ///     using directives (e.g. from <c>ExtractTestClass</c>); new ones are merged into the
    ///     existing file's using block, duplicates are skipped.
    /// </param>
    public void AppendMethodToExistingClassFile(string filePath, string methodCode)
    {
        var existingCode = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(existingCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl == null)
        {
            return;
        }

        var (extractedUsings, methodsText) = SplitUsingsFromMethods(methodCode);
        if (string.IsNullOrWhiteSpace(methodsText))
        {
            throw new ArgumentException("Generated code does not contain a test method.", nameof(methodCode));
        }

        var methodRoot = CSharpSyntaxTree.ParseText($"class GeneratedTestContainer {{ {methodsText} }}").GetCompilationUnitRoot();
        var newMethodNode = methodRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        if (newMethodNode == null)
        {
            return;
        }

        if (classDecl.Members.OfType<MethodDeclarationSyntax>().Any(existingMethod => HasSameSignature(existingMethod, newMethodNode)))
        {
            return;
        }

        var usings = root.Usings.ToList();
        var existingUsings = new HashSet<string>(usings.Select(GetUsingKey), StringComparer.Ordinal);

        foreach (var usingText in extractedUsings)
        {
            var parsedUsing = CSharpSyntaxTree.ParseText(usingText).GetCompilationUnitRoot().Usings.FirstOrDefault();
            if (parsedUsing != null && existingUsings.Add(GetUsingKey(parsedUsing)))
            {
                usings.Add(parsedUsing);
            }
        }

        var updatedClass = classDecl.AddMembers(newMethodNode);
        var newRoot = root.ReplaceNode(classDecl, updatedClass).WithUsings(SyntaxFactory.List(usings));

        var formattedRoot = Formatter.Format(newRoot, new AdhocWorkspace());
        File.WriteAllText(filePath, formattedRoot.ToFullString());
    }

    private static string GetUsingKey(UsingDirectiveSyntax usingDirective)
    {
        var normalized = usingDirective.WithoutTrivia();
        return string.Join("|", normalized.GlobalKeyword.RawKind, normalized.StaticKeyword.RawKind, normalized.Alias?.Name.ToString(),
            normalized.Name?.ToString());
    }

    private static bool HasSameSignature(MethodDeclarationSyntax first, MethodDeclarationSyntax second)
    {
        if (!string.Equals(first.Identifier.ValueText, second.Identifier.ValueText, StringComparison.Ordinal) ||
            (first.TypeParameterList?.Parameters.Count ?? 0) != (second.TypeParameterList?.Parameters.Count ?? 0) ||
            first.ParameterList.Parameters.Count != second.ParameterList.Parameters.Count)
        {
            return false;
        }

        for (var i = 0; i < first.ParameterList.Parameters.Count; i++)
        {
            var firstParameter = first.ParameterList.Parameters[i];
            var secondParameter = second.ParameterList.Parameters[i];
            if (!string.Equals(firstParameter.Type?.WithoutTrivia().ToString(), secondParameter.Type?.WithoutTrivia().ToString(),
                    StringComparison.Ordinal) ||
                !string.Equals(GetRefKind(firstParameter), GetRefKind(secondParameter), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetRefKind(ParameterSyntax parameter)
    {
        foreach (var modifier in parameter.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.RefKeyword) || modifier.IsKind(SyntaxKind.OutKeyword) || modifier.IsKind(SyntaxKind.InKeyword))
            {
                return modifier.ValueText;
            }
        }

        return string.Empty;
    }

    /// <summary>
    ///     Zerlegt einen Code-Snippet in Compilation-Unit-usings und Methodentext.
    ///     Wird von <see cref="CreateNewTestClassFile" /> verwendet, um die usings aus
    ///     dem zuvor von <c>ExtractTestClass</c> erzeugten Text wieder an die richtige
    ///     Stelle zu heben.
    /// </summary>
    /// <returns>
    ///     <c>Usings</c>: vollständige using-Zeilen (inkl. Semikolon), dedupliziert.
    ///     <c>Methods</c>: Methodendeklarationen ohne usings, oder leer wenn keine
    ///     Methodendeklaration erkannt wird. Vollständige Klassen-/Namespace-Wrapper
    ///     werden niemals als Methodeninhalt weitergereicht.
    /// </returns>
    private static (List<string> Usings, string Methods) SplitUsingsFromMethods(string code)
    {
        var usings = new List<string>();
        if (string.IsNullOrWhiteSpace(code))
        {
            return (usings, string.Empty);
        }

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        foreach (var u in root.Usings)
        {
            var text = u.ToFullString().TrimEnd();
            if (!usings.Contains(text, StringComparer.Ordinal))
            {
                usings.Add(text);
            }
        }

        var methods = GetDistinctMethods(root);

        if (methods.Count == 0)
        {
            var classMembers = code;
            foreach (var usingDirective in root.Usings.OrderByDescending(directive => directive.SpanStart))
            {
                classMembers = classMembers.Remove(usingDirective.SpanStart, usingDirective.Span.Length);
            }

            var wrappedRoot = CSharpSyntaxTree.ParseText($"class GeneratedTestContainer {{ {classMembers} }}").GetCompilationUnitRoot();
            methods = GetDistinctMethods(wrappedRoot);
        }

        if (methods.Count == 0)
        {
            return (usings, string.Empty);
        }

        var sb = new StringBuilder();
        foreach (var m in methods)
        {
            sb.AppendLine(m.ToFullString());
        }

        return (usings, sb.ToString().Trim());
    }

    private static List<MethodDeclarationSyntax> GetDistinctMethods(CompilationUnitSyntax root)
    {
        var methods = new List<MethodDeclarationSyntax>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!methods.Any(existingMethod => HasSameSignature(existingMethod, method)))
            {
                methods.Add(method);
            }
        }

        return methods;
    }
}