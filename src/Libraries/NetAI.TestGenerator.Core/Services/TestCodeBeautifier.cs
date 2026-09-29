using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Text.RegularExpressions;
using NetAI.TestGenerator.Core.Models.Enums;

namespace NetAI.TestGenerator.Core.Services;


/// <summary>
/// Fügt fehlende using-Direktiven hinzu (abhängig von Test-/Mock-Framework
/// und anhand von Code-Mustern) und formatiert den C#-Code mit Roslyn.
/// Optional können auch Compiler-Fehler (fehlende Typen) automatisch gefixt werden.
/// </summary>
public class TestCodeBeautifier
{
    // Ein Workspace pro Instanz reicht. AdhocWorkspace ist nicht threadsafe,
    // die Verwendung hier ist aber single-threaded (innerhalb der Schleife).
    private readonly AdhocWorkspace _workspace = new();

    // -----------------------------------------------------------------
    //  Öffentliche API
    // -----------------------------------------------------------------

    /// <summary>
    /// Fügt die notwendigen usings hinzu und formatiert den Code.
    /// </summary>
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

        // Wenn der Code nicht einmal parsebar ist, unverändert zurückgeben –
        // dann soll der Compiler-Check bzw. die AI-Reparatur übernehmen.
        if (tree.GetDiagnostics(cancellationToken)
                .Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return sourceCode;
        }

        var root = tree.GetCompilationUnitRoot(cancellationToken);

        // 1. Usings ergänzen (Framework + Muster-Erkennung)
        root = AddRequiredUsings(root, testFramework, mockFramework);

        // 2. Usings sortieren
        root = SortUsings(root);

        // 3. Code formatieren
        root = await FormatRootAsync(root, cancellationToken);

        return root.ToFullString();
    }

    /// <summary>
    /// Versucht, typische Compiler-Fehler (hauptsächlich fehlende usings)
    /// anhand der Fehlertexte automatisch zu beheben.
    /// </summary>
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

    // -----------------------------------------------------------------
    //  Interne Helfer
    // -----------------------------------------------------------------

    private static CompilationUnitSyntax AddRequiredUsings(
        CompilationUnitSyntax root,
        TestFramework testFramework,
        MockFramework mockFramework)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);

        // Test-Framework
        switch (testFramework)
        {
            case TestFramework.NUnit: required.Add("NUnit.Framework"); break;
            case TestFramework.xUnit: required.Add("Xunit"); break;
            case TestFramework.MSTest: required.Add("Microsoft.VisualStudio.TestTools.UnitTesting"); break;
        }

        // Explizit angegebenes Mock-Framework
        switch (mockFramework)
        {
            case MockFramework.Moq: required.Add("Moq"); break;
            case MockFramework.NSubstitute: required.Add("NSubstitute"); break;
            case MockFramework.FakeItEasy: required.Add("FakeItEasy"); break;
        }

        // --- Muster-Erkennung im Code ---
        var code = root.ToFullString();

        if (Regex.IsMatch(code, @"\bMock\s*<") ||
            Regex.IsMatch(code, @"\bIt\s*\.\s*IsAny\s*<"))
            required.Add("Moq");

        if (Regex.IsMatch(code, @"\bSubstitute\s*\.\s*For\s*<"))
            required.Add("NSubstitute");

        if (Regex.IsMatch(code, @"\bA\s*\.\s*Fake\s*<"))
            required.Add("FakeItEasy");

        // Standard-Usings, die in fast jedem Test gebraucht werden
        required.Add("System");
        required.Add("System.Threading.Tasks");

        // Nur die Usings hinzufügen, die noch fehlen
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

        // "The type or namespace name 'X' could not be found ..."
        var m1 = Regex.Match(
            errorMessage,
            @"type or namespace name '([^']+)' could not be found",
            RegexOptions.IgnoreCase);
        if (m1.Success)
        {
            typeName = m1.Groups[1].Value;
            return true;
        }

        // "The name 'X' does not exist in the current context"
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
            "Regex" => "System.Text.RegularExpressions",
            _ => null
        };
    }
}