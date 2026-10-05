using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace NetAI.TestGenerator.Core.Analysis;

public class UnitTestSkeletonGenerator
{
    public record GeneratorResult(string TestSkeleton, string AiPromptContext);

    /// <summary>
    /// Generiert ein robustes xUnit+Moq-Test-Skelett und den passenden AI-Prompt-Kontext
    /// für eine beliebige C#-Klasse (inkl. .NET 6+, Primary Constructors und XAML Code-Behind).
    /// </summary>
    public static GeneratorResult Generate(string sourceCode, Compilation compilation)
    {
        try
        {
            // 1. Quellcode parsen und semantisches Modell vorbereiten
            SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceCode);
            var updatedCompilation = compilation.AddSyntaxTrees(tree);
            SemanticModel semanticModel = updatedCompilation.GetSemanticModel(tree);

            var classDeclaration = tree.GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDeclaration == null)
                return CreateTotalFallback("Keine Klasse im bereitgestellten Quellcode gefunden.");

            // FALLBACK 1: Statische oder abstrakte Klassen abfangen (Können nicht per 'new' instanziiert werden)
            if (classDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.AbstractKeyword)))
            {
                return CreateStaticOrAbstractFallback(classDeclaration);
            }

            string className = classDeclaration.Identifier.Text;

            // 2. Konstruktor-Parameter ermitteln (.NET 8+ Primary Constructor vs. klassischer Konstruktor)
            var hasPrimaryConstructor = classDeclaration.ParameterList != null;
            var constructors = classDeclaration.DescendantNodes().OfType<ConstructorDeclarationSyntax>().ToList();

            List<ParameterSyntax> parameters = new();
            if (hasPrimaryConstructor)
            {
                // C# 12+: Parameter hängen direkt an der Klassendefinition
                parameters = classDeclaration.ParameterList!.Parameters.ToList();
            }
            else if (constructors.Any())
            {
                // Traditionell: Nimm den public Konstruktor mit den meisten Parametern (bestes DI-Target)
                var bestConstructor = constructors
                    .Where(c => c.Modifiers.Any(SyntaxKind.PublicKeyword))
                    .OrderByDescending(c => c.ParameterList.Parameters.Count)
                    .FirstOrDefault();

                parameters = bestConstructor?.ParameterList.Parameters.ToList() ?? new List<ParameterSyntax>();
            }

            // FALLBACK 2: Keine Konstruktoren oder parameterlos (z.B. XAML Views, reine DTOs)
            if (!parameters.Any())
            {
                return CreateParameterlessSkeleton(classDeclaration);
            }

            // 3. Mocks, Initialisierungen und Prompt-Kontext aufbauen
            var mockFields = new List<string>();
            var mockInitializations = new List<string>();
            var sutConstructorArgs = new List<string>();
            var interfaceContextBuilder = new StringBuilder();

            foreach (var param in parameters)
            {
                string paramName = param.Identifier.Text;
                string fieldName = $"_{paramName}Mock";

                // Textueller Standardtyp aus dem Quellcode (Fallback falls Semantik fehlschlägt)
                string typeName = param.Type?.ToString() ?? "object";

                // Semantische Analyse: Versuche den exakten Typen über das Projekt aufzulösen
                if (semanticModel.GetDeclaredSymbol(param) is IParameterSymbol parameterSymbol)
                {
                    typeName = parameterSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    ExtractMethodsForPrompt(parameterSymbol.Type, fieldName, interfaceContextBuilder);
                }
                else
                {
                    // FALLBACK 3: Typ im Projekt nicht auflösbar (z.B. fehlende NuGet-Referenz in Compilation)
                    interfaceContextBuilder.AppendLine($"\nMock-Objekt: `{fieldName}` (Typ: {typeName}) - [Hinweis: Methoden konnten nicht semantisch ausgelesen werden]");
                }

                mockFields.Add($"    private readonly Mock<{typeName}> {fieldName};");
                mockInitializations.Add($"        {fieldName} = new Mock<{typeName}>();");
                sutConstructorArgs.Add($"{fieldName}.Object");
            }

            // Erste öffentliche Methode als Platzhalter-Name für das LLM finden
            var firstMethod = classDeclaration.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Modifiers.Any(SyntaxKind.PublicKeyword));
            string targetMethodName = firstMethod?.Identifier.Text ?? "Execute";

            string sutArgsJoined = string.Join(", ", sutConstructorArgs);
            string skeleton = BuildSkeletonStructure(classDeclaration, className, mockFields, mockInitializations, sutArgsJoined, targetMethodName);

            return new GeneratorResult(skeleton, interfaceContextBuilder.ToString());
        }
        catch (Exception ex)
        {
            // CRITICAL FALLBACK: Totalschaden verhindern. Ein leeres Skelett ausgeben, damit die Pipeline nicht crasht
            return CreateTotalFallback($"Kritischer Fehler bei der Roslyn-Analyse: {ex.Message}");
        }
    }

    private static void ExtractMethodsForPrompt(ITypeSymbol typeSymbol, string fieldName, StringBuilder contextBuilder)
    {
        contextBuilder.AppendLine($"\nVerfügbares Mock-Objekt im Test: `{fieldName}` (Typ: {typeSymbol.Name})");
        contextBuilder.AppendLine("Verfügbare Methoden für dein `.Setup(...)`:");

        // Holt alle Members, inklusive geerbter Methoden von Basis-Interfaces
        var allMembers = typeSymbol.GetMembers().Concat(typeSymbol.AllInterfaces.SelectMany(i => i.GetMembers()));
        var methods = allMembers.OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public);

        if (!methods.Any())
        {
            contextBuilder.AppendLine("- (Keine öffentlichen Methoden gefunden)");
            return;
        }

        foreach (var method in methods)
        {
            string signature = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            contextBuilder.AppendLine($"- `{signature}`");
        }
    }

    private static string BuildSkeletonStructure(ClassDeclarationSyntax classDecl, string className, List<string> fields, List<string> inits, string args, string methodName)
    {
        var namespaceDecl = classDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        string ns = namespaceDecl?.Name.ToString() ?? "YourProject";

        return $@"using Moq;
using FluentAssertions;
using Xunit;

namespace {ns}.UnitTests;

public class {className}Tests
{{
{string.Join(Environment.NewLine, fields)}
    private readonly {className} _sut;

    public {className}Tests()
    {{
{string.Join(Environment.NewLine, inits)}

        _sut = new {className}({args});
    }}

    [Fact]
    public async Task {methodName}_WhenCalled_ShouldBehavior()
    {{
        // =========================================================================
        // AI BEREICH: Nur diesen Inhalt lässt du von der AI generieren!
        // =========================================================================
        
        // Arrange
        
        // Act
        
        // Assert
        
        // =========================================================================
    }}
}}";
    }

    private static GeneratorResult CreateParameterlessSkeleton(ClassDeclarationSyntax classDecl)
    {
        string className = classDecl.Identifier.Text;
        string ns = classDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "YourProject";

        string skeleton = $@"using FluentAssertions;
using Xunit;

namespace {ns}.UnitTests;

public class {className}Tests
{{
    private readonly {className} _sut;

    public {className}Tests()
    {{
        _sut = new {className}();
    }}

    [Fact]
    public void Test_Placeholder()
    {{
        // AI BEREICH
    }}
}}";
        return new GeneratorResult(skeleton, "Klasse besitzt keine Konstruktor-Parameter. Keine Mock-Objekte verfügbar.");
    }

    private static GeneratorResult CreateStaticOrAbstractFallback(ClassDeclarationSyntax classDecl)
    {
        string className = classDecl.Identifier.Text;
        string ns = classDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "YourProject";

        string skeleton = $@"using FluentAssertions;
using Xunit;

namespace {ns}.UnitTests;

public class {className}Tests
{{
    // Hinweis: Klasse ist statisch oder abstrakt. Kann nicht instanziiert werden.
    // Rufe Methoden im Test direkt über '{className}.MethodenName()' auf.

    [Fact]
    public void Test_Placeholder()
    {{
        // AI BEREICH
    }}
}}";
        return new GeneratorResult(skeleton, $"Klasse ist statisch oder abstrakt. Rufe die Methoden im Test direkt statisch über {className} auf.");
    }

    private static GeneratorResult CreateTotalFallback(string errorMessage)
    {
        string skeleton = $@"using FluentAssertions;
using Xunit;

namespace YourProject.UnitTests;

public class AutomatedTests
{{
    // {errorMessage}
    // Fallback aktiviert: Bitte erstelle die Instanziierung und Mocks vollständig selbst.

    [Fact]
public void Test_Placeholder()
{{
// AI BEREICH
}}
}}";
        return new GeneratorResult(skeleton, $"WARNUNG: {errorMessage}. Du musst den gesamten Test von Grund auf selbst schreiben.");
    }
}