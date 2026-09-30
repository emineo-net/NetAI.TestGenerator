using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Text.RegularExpressions;
using NetAI.TestGenerator.Core.Models.Enums;

namespace NetAI.TestGenerator.Core.Services;

public class TestCodeBeautifier
{
    private readonly AdhocWorkspace _workspace = new();

    public async Task<string> BeautifyAndAddUsingsAsync(
        string sourceCode,
        TestFramework testFramework = TestFramework.xUnit,
        MockFramework mockFramework = MockFramework.Unknown,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            return sourceCode;

        var tree = CSharpSyntaxTree.ParseText(
            sourceCode,
            new CSharpParseOptions(LanguageVersion.Latest),
            cancellationToken: cancellationToken);

        if (tree.GetDiagnostics(cancellationToken)
                .Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return sourceCode;
        }

        var root = tree.GetCompilationUnitRoot(cancellationToken);

        root = AddRequiredUsings(root, testFramework, mockFramework);

        root = SortUsings(root);

        root = await FormatRootAsync(root, cancellationToken);

        return root.ToFullString();
    }

    public async Task<string> TryFixCompilerErrorsAsync(
        string sourceCode,
        IEnumerable<string> compilerErrors,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            return sourceCode;

        var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
        var root = tree.GetCompilationUnitRoot(cancellationToken);

        bool changed = false;

        foreach (var error in compilerErrors ?? Enumerable.Empty<string>())
        {
            if (TryExtractMissingType(error, out var missingType))
            {
                var ns = MapTypeToNamespace(missingType);
                if (ns != null && !HasUsing(root, ns))
                {
                    root = root.AddUsings(
                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
                            .NormalizeWhitespace());
                    changed = true;
                }
            }
        }

        if (!changed)
            return sourceCode;

        root = SortUsings(root);
        root = await FormatRootAsync(root, cancellationToken);
        return root.ToFullString();
    }

    private static CompilationUnitSyntax AddRequiredUsings(
        CompilationUnitSyntax root,
        TestFramework testFramework,
        MockFramework mockFramework)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);

        switch (testFramework)
        {
            case TestFramework.NUnit: required.Add("NUnit.Framework"); break;
            case TestFramework.xUnit: required.Add("Xunit"); break;
            case TestFramework.MSTest: required.Add("Microsoft.VisualStudio.TestTools.UnitTesting"); break;
        }

        switch (mockFramework)
        {
            case MockFramework.Moq: required.Add("Moq"); break;
            case MockFramework.NSubstitute: required.Add("NSubstitute"); break;
            case MockFramework.FakeItEasy: required.Add("FakeItEasy"); break;
        }

        var code = root.ToFullString();

        if (Regex.IsMatch(code, @"\bMock\s*<") ||
            Regex.IsMatch(code, @"\bIt\s*\.\s*IsAny\s*<"))
            required.Add("Moq");

        if (Regex.IsMatch(code, @"\bSubstitute\s*\.\s*For\s*<"))
            required.Add("NSubstitute");

        if (Regex.IsMatch(code, @"\bA\s*\.\s*Fake\s*<"))
            required.Add("FakeItEasy");

        required.Add("System");
        required.Add("System.Threading.Tasks");

        var missing = required.Where(ns => !HasUsing(root, ns)).ToList();
        if (missing.Count == 0)
            return root;

        var newUsings = missing
            .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
                .NormalizeWhitespace())
            .ToArray();

        return root.AddUsings(newUsings);
    }

    private static bool HasUsing(CompilationUnitSyntax root, string namespaceName)
    {
        return root.Usings.Any(u =>
            string.Equals(u.Name?.ToString(), namespaceName, StringComparison.Ordinal));
    }

    private static CompilationUnitSyntax SortUsings(CompilationUnitSyntax root)
    {
        var sorted = root.Usings
            .OrderBy(u => u.Name?.ToString(), StringComparer.Ordinal)
            .Select(u => u.NormalizeWhitespace())
            .ToArray();

        return root.WithUsings(SyntaxFactory.List(sorted));
    }

    private async Task<CompilationUnitSyntax> FormatRootAsync(
        CompilationUnitSyntax root,
        CancellationToken cancellationToken)
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = _workspace.CurrentSolution
            .AddProject(projectId, "FormatProject", "FormatProject", LanguageNames.CSharp)
            .AddDocument(documentId, "Code.cs", root.ToFullString());

        var document = solution.GetDocument(documentId)!;
        var formattedDoc = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
        var formattedRoot = await formattedDoc.GetSyntaxRootAsync(cancellationToken);

        return (CompilationUnitSyntax)formattedRoot!;
    }

    private static bool TryExtractMissingType(string errorMessage, out string typeName)
    {
        typeName = string.Empty;
        if (string.IsNullOrWhiteSpace(errorMessage)) return false;

        var m1 = Regex.Match(
            errorMessage,
            @"type or namespace name '([^']+)' could not be found",
            RegexOptions.IgnoreCase);
        if (m1.Success)
        {
            typeName = m1.Groups[1].Value;
            return true;
        }

        var m2 = Regex.Match(
            errorMessage,
            @"The name '([^']+)' does not exist in the current context",
            RegexOptions.IgnoreCase);
        if (m2.Success)
        {
            typeName = m2.Groups[1].Value;
            return true;
        }

        return false;
    }

    private static string? MapTypeToNamespace(string typeName)
    {
        return typeName switch
        {
            "Mock" or "It" or "Times" or "MockBehavior" => "Moq",
            "Substitute" or "Arg" or "Received" => "NSubstitute",
            "A" or "Fake" => "FakeItEasy",
            "Fact" or "Theory" or "InlineData" or "Assert" => "Xunit",
            "Test" or "TestClass" or "TestMethod" => "NUnit.Framework",
            "Task" => "System.Threading.Tasks",
            "List" or "Dictionary" or "IEnumerable" or "IReadOnlyList" => "System.Collections.Generic",
            "BindingFlags" or "MemberInfo" or "MethodBase" or "MethodInfo"
                or "ConstructorInfo" or "PropertyInfo" or "FieldInfo" or "EventInfo"
                or "ParameterInfo" or "Assembly" or "Module" or "TargetException"
                or "TargetInvocationException" => "System.Reflection",
            "Regex" => "System.Text.RegularExpressions",
            _ => null
        };
    }
}