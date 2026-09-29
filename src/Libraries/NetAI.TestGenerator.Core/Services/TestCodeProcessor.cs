using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using NetAI.TestGenerator.Core.Models.Enums;
using NetAI.TestGenerator.Core.Services;
using System.Collections.Immutable;
using System.Text;

namespace NetAI.TestGenerator.Core.Services;

public class TestCodeProcessor
{
    private readonly ICompilerService _compilerService;
    private readonly AdhocWorkspace _workspace;

    public TestCodeProcessor(ICompilerService compilerService)
    {
        _compilerService = compilerService ?? throw new ArgumentNullException(nameof(compilerService));

        // Ein AdhocWorkspace ist notwendig, damit Roslyn Formatierungs- und
        // Workspace-Operationen (z. B. Formatter.FormatAsync) ausführen kann.
        _workspace = new AdhocWorkspace();
    }

    // -----------------------------------------------------------------
    //  Öffentliche Einstiegspunkt‑Methode
    // -----------------------------------------------------------------

    /// <summary>
    /// Verarbeitet den übergebenen Testklassen-Code vollständig:
    /// 1. Fehlende Usings hinzufügen (abhängig von Test-/Mock-Framework)
    /// 2. Code mit Roslyn formatieren
    /// 3. Compiler aufrufen und Diagnosen abrufen
    /// 4. Compiler-Fehler so weit wie möglich automatisch beheben
    /// </summary>
    /// <param name="sourceCode">Der ursprüngliche C#-Code der Testklasse.</param>
    /// <param name="testFramework">Das verwendete Test-Framework.</param>
    /// <param name="mockFramework">Das verwendete Mock-Framework.</param>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    /// <returns>Der optimierte Quellcode.</returns>
    public async Task<string> ProcessTestClassAsync(
        string sourceCode,
        TestFramework testFramework,
        MockFramework mockFramework,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            throw new ArgumentException("Der Quellcode darf nicht leer sein.", nameof(sourceCode));

        // 1. Syntaxbaum aus dem übergebenen Code erzeugen
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            sourceCode,
            new CSharpParseOptions(LanguageVersion.Latest),
            cancellationToken: cancellationToken);

        CompilationUnitSyntax root = (CompilationUnitSyntax)await syntaxTree.GetRootAsync(cancellationToken);

        // 2. Fehlende Usings hinzufügen
        root = AddRequiredUsings(root, testFramework, mockFramework);

        // 3. Code formatieren (Beautify)
        Document document = CreateDocumentFromRoot(root, cancellationToken);
        document = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
        root = (CompilationUnitSyntax)await document.GetSyntaxRootAsync(cancellationToken);

        // 4. Compiler aufrufen und Diagnosen abrufen
        string currentCode = root.ToFullString();
        IReadOnlyList<Diagnostic> diagnostics = await _compilerService
            .CompileAndGetDiagnosticsAsync(currentCode, cancellationToken);

        // 5. Compiler-Fehler so weit wie möglich beheben
        int maxFixIterations = 5; // Sicherheitsgrenze, um Endlosschleifen zu vermeiden
        int iteration = 0;

        while (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) &&
               iteration < maxFixIterations)
        {
            iteration++;

            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            bool anyFixed = false;

            foreach (var error in errors)
            {
                // Fehlende Usings, die der Compiler meldet, können oft automatisch ergänzt werden
                if (error.Id == "CS0246" || error.Id == "CS0234" || error.Id == "CS0103")
                {
                    // Der Compiler hat uns bereits die nötigen Informationen geliefert.
                    // In diesem Beispiel versuchen wir, den fehlenden Typ über den Namen zu finden.
                    // In einem echten Szenario würden wir hier die Semantik nutzen.
                    anyFixed |= TryFixMissingUsing(root, error, ref root);
                }
            }

            // Wenn wir nichts geändert haben, können wir die Schleife abbrechen.
            if (!anyFixed)
                break;

            // Nach der Änderung erneut formatieren und kompilieren
            document = CreateDocumentFromRoot(root, cancellationToken);
            document = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
            root = (CompilationUnitSyntax)await document.GetSyntaxRootAsync(cancellationToken);

            currentCode = root.ToFullString();
            diagnostics = await _compilerService
                .CompileAndGetDiagnosticsAsync(currentCode, cancellationToken);
        }

        // 6. Endergebnis zurückgeben
        return root.ToFullString();
    }

    // -----------------------------------------------------------------
    //  Hilfsmethoden
    // -----------------------------------------------------------------

    /// <summary>
    /// Fügt die für das angegebene Test- und Mock-Framework notwendigen
    /// using-Direktiven hinzu, falls sie noch nicht vorhanden sind.
    /// </summary>
    private static CompilationUnitSyntax AddRequiredUsings(
        CompilationUnitSyntax root,
        TestFramework testFramework,
        MockFramework mockFramework)
    {
        var requiredUsings = new HashSet<string>(StringComparer.Ordinal);

        // --- Test-Framework-spezifische Usings ---
        switch (testFramework)
        {
            case TestFramework.NUnit:
                requiredUsings.Add("NUnit.Framework");
                break;
            case TestFramework.xUnit:
                requiredUsings.Add("Xunit");
                break;
            case TestFramework.MSTest:
                requiredUsings.Add("Microsoft.VisualStudio.TestTools.UnitTesting");
                break;
        }

        // --- Mock-Framework-spezifische Usings ---
        switch (mockFramework)
        {
            case MockFramework.Moq:
                requiredUsings.Add("Moq");
                break;
            case MockFramework.NSubstitute:
                requiredUsings.Add("NSubstitute");
                break;
            case MockFramework.FakeItEasy:
                requiredUsings.Add("FakeItEasy");
                break;
        }

        // Vorhandene Usings sammeln, um Duplikate zu vermeiden
        var existingNames = new HashSet<string>(
            root.Usings
                .Select(u => u.Name?.ToString())
                .Where(n => n != null)!,
            StringComparer.Ordinal);

        var usingsToAdd = requiredUsings
            .Where(ns => !existingNames.Contains(ns))
            .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
                                            .NormalizeWhitespace())
            .ToArray();

        if (usingsToAdd.Length > 0)
        {
            // AddUsings hängt die neuen using-Direktiven an die Liste der
            // bereits vorhandenen an. Die Methode ist Teil von
            // CompilationUnitSyntax und arbeitet mit unveränderlichen Knoten.
            root = root.AddUsings(usingsToAdd);
        }

        return root;
    }

    /// <summary>
    /// Erstellt ein Roslyn-Dokument aus einem CompilationUnitSyntax-Knoten.
    /// Dies ist notwendig, weil einige APIs (z. B. Formatter.FormatAsync)
    /// ein Document erwarten.
    /// </summary>
    private Document CreateDocumentFromRoot(
        CompilationUnitSyntax root,
        CancellationToken cancellationToken)
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = _workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
            .AddDocument(documentId, "TestClass.cs", root.ToFullString());

        return solution.GetDocument(documentId)!;
    }

    /// <summary>
    /// Versucht, einen vom Compiler gemeldeten Fehler zu beheben.
    /// In diesem Beispiel wird nur der Fall "fehlendes using" behandelt.
    /// </summary>
    private static bool TryFixMissingUsing(
        CompilationUnitSyntax root,
        Diagnostic error,
        ref CompilationUnitSyntax updatedRoot)
    {
        // Der Diagnostic liefert uns den Namen des Typs, der nicht gefunden wurde.
        // In der Praxis würden wir hier die SemanticModel des Compilations nutzen,
        // um den vollständigen Namespace zu ermitteln. Aus Platzgründen wird hier
        // nur ein einfaches Beispiel gezeigt.

        // Beispiel: Wenn der Fehler "CS0246: The type or namespace name 'X' could not be found"
        // lautet, könnten wir versuchen, 'X' als using hinzuzufügen.
        // Dies ist stark vereinfacht und dient nur der Demonstration.

        // In einem echten Szenario würden wir die Message des Diagnostics parsen
        // und den fehlenden Typ ermitteln.
        // Für dieses Beispiel geben wir einfach false zurück, wenn wir nicht sicher sind.
        return false;
    }
}