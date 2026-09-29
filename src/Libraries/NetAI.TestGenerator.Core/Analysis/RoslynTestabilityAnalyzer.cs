using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;   // <- MSBuildWorkspace
using Microsoft.Build.Locator;          // <- MSBuildLocator



namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>
/// Analysiert eine Methode per Roslyn und liefert einen kompakten,
/// semantisch korrekten "Faktenbericht", der als zusätzlicher Kontext
/// an das LLM übergeben wird. Ziel: Das LLM soll keine Typen, Interfaces
/// oder Properties mehr erfinden.
///
/// WICHTIG: Vor dem ersten Aufruf muss einmalig
///   MSBuildLocator.RegisterDefaults();
/// aufgerufen werden (z. B. im Program-Einstieg).
/// </summary>
public sealed class RoslynTestabilityAnalyzer
{
    private static readonly SymbolDisplayFormat FqFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    private readonly AnalyzerOptions _options;

    public RoslynTestabilityAnalyzer(AnalyzerOptions? options = null)
        => _options = options ?? new AnalyzerOptions();

    // ------------------------------------------------------------------
    //  Public API
    // ------------------------------------------------------------------

    public async Task<TestabilityReport> AnalyzeFromSolutionAsync(
        string solutionPath,
        string documentName,
        string methodName,
        CancellationToken ct = default)
    {
        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

        var document = FindDocument(solution, documentName)
            ?? throw new InvalidOperationException(
                $"Dokument '{documentName}' wurde in der Solution nicht gefunden.");

        return await AnalyzeDocumentAsync(document, methodName, ct);
    }

    public async Task<TestabilityReport> AnalyzeFromProjectAsync(
        string projectPath,
        string documentName,
        string methodName,
        CancellationToken ct = default)
    {
        using var workspace = MSBuildWorkspace.Create();
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);

        var document = FindDocument(project, documentName)
            ?? throw new InvalidOperationException(
                $"Dokument '{documentName}' wurde im Projekt nicht gefunden.");

        return await AnalyzeDocumentAsync(document, methodName, ct);
    }

    public async Task<TestabilityReport> AnalyzeDocumentAsync(
        Document document,
        string methodName,
        CancellationToken ct = default)
    {
        var compilation = await document.Project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException("Compilation konnte nicht erstellt werden.");
        var tree = await document.GetSyntaxTreeAsync(ct)
            ?? throw new InvalidOperationException("SyntaxTree fehlt.");
        var model = compilation.GetSemanticModel(tree);

        var methodDecl = tree.GetRoot(ct)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName)
            ?? throw new InvalidOperationException(
                $"Methode '{methodName}' wurde im Dokument nicht gefunden.");

        var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol
                           ?? throw new InvalidOperationException("Kein Methodensymbol gefunden.");

        return BuildReport(document, methodSymbol, methodDecl, model, compilation);
    }

    // ------------------------------------------------------------------
    //  Report-Aufbau
    // ------------------------------------------------------------------

    private TestabilityReport BuildReport(
        Document document,
        IMethodSymbol method,
        MethodDeclarationSyntax syntax,
        SemanticModel model,
        Compilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(_options.MaxDiagnostics)
            .Select(d => d.ToString())
            .ToList();

        var methodFact = BuildMethodFact(method);
        var typeFacts = CollectReferencedTypes(syntax, model);
        var verdict = EvaluateTestability(methodFact, typeFacts);
        var recs = BuildRecommendations(methodFact, typeFacts);

        return new TestabilityReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            DocumentName = document.Name,
            Method = methodFact,
            ReferencedTypes = typeFacts,
            Verdict = verdict,
            Recommendations = recs,
            CompilationErrors = errors
        };
    }

    private static MethodFact BuildMethodFact(IMethodSymbol method)
    {
        var containing = method.ContainingType;

        return new MethodFact
        {
            Name = method.Name,
            Signature = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ReturnType = method.ReturnType.ToDisplayString(FqFormat),
            Accessibility = method.DeclaredAccessibility.ToString(),
            IsStatic = method.IsStatic,
            IsAsync = method.IsAsync,
            ReturnsVoid = method.ReturnsVoid,
            IsAsyncVoid = method.IsAsync && method.ReturnsVoid,
            ContainingType = new TypeFact
            {
                FullName = containing.ToDisplayString(FqFormat),
                Namespace = containing.ContainingNamespace?.ToDisplayString() ?? "",
                Kind = KindOf(containing),
                Accessibility = containing.DeclaredAccessibility.ToString(),
                IsStatic = containing.IsStatic,
                IsSealed = containing.IsSealed,
                IsAbstract = containing.IsAbstract,
                IsInterface = containing.TypeKind == TypeKind.Interface,
                Mockable = ClassMockability(containing),
                UsedStatically = false,
                Interfaces = containing.AllInterfaces.Select(i => i.ToDisplayString(FqFormat)).ToList(),
                Constructors = containing.InstanceConstructors
                    .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                    .Select(c => c.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                    .ToList()
            },
            Parameters = method.Parameters.Select(p => new ParameterFact
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(FqFormat),
                IsOptional = p.IsOptional,
                HasDefaultValue = p.HasExplicitDefaultValue,
                DefaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
            }).ToList()
        };
    }

    private List<TypeFact> CollectReferencedTypes(
        MethodDeclarationSyntax syntax,
        SemanticModel model)
    {
        var seen = new Dictionary<string, TypeFact>(StringComparer.Ordinal);

        void Add(ITypeSymbol? symbol, bool staticUse = false)
        {
            if (symbol is null) return;
            if (symbol is ITypeParameterSymbol) return;
            if (symbol.SpecialType != SpecialType.None) return; // int, string, ...

            if (symbol is not INamedTypeSymbol named) return;

            if (named.IsGenericType)
            {
                foreach (var arg in named.TypeArguments)
                    Add(arg, staticUse);
            }

            var full = named.OriginalDefinition.ToDisplayString(FqFormat);
            if (full.StartsWith("System.Nullable", StringComparison.Ordinal)) return;

            var isFramework = named.ContainingNamespace?.ToDisplayString()
                .StartsWith("System", StringComparison.Ordinal) == true;

            if (isFramework && !_options.IncludeFrameworkTypes && !staticUse)
                return;

            if (seen.TryGetValue(full, out var existing))
            {
                if (staticUse) existing.UsedStatically = true;
                return;
            }

            var fact = new TypeFact
            {
                FullName = full,
                Namespace = named.ContainingNamespace?.ToDisplayString() ?? "",
                Kind = KindOf(named),
                Accessibility = named.DeclaredAccessibility.ToString(),
                IsStatic = named.IsStatic,
                IsSealed = named.IsSealed,
                IsAbstract = named.IsAbstract,
                IsInterface = named.TypeKind == TypeKind.Interface,
                Mockable = ClassMockability(named),
                UsedStatically = staticUse,
                Constructors = named.InstanceConstructors
                    .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                    .Select(c => c.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                    .ToList(),
                Interfaces = named.AllInterfaces.Select(i => i.ToDisplayString(FqFormat)).ToList(),
                VirtualMemberCount = named.GetMembers().Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride),
                MemberCount = named.GetMembers().Length
            };

            seen[full] = fact;
        }

        foreach (var node in syntax.DescendantNodes())
        {
            switch (node)
            {
                case ObjectCreationExpressionSyntax oc:
                    Add(model.GetTypeInfo(oc).Type);
                    break;

                case InvocationExpressionSyntax inv:
                    if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol mi)
                    {
                        Add(mi.ContainingType, mi.IsStatic);
                        Add(mi.ReturnType, mi.IsStatic);
                    }
                    break;

                case MemberAccessExpressionSyntax ma:
                    // z. B. File.ReadAllText -> File als statische Abhängigkeit
                    if (model.GetSymbolInfo(ma).Symbol is IMethodSymbol m)
                        Add(m.ContainingType, m.IsStatic);
                    else if (model.GetSymbolInfo(ma).Symbol is IPropertySymbol p)
                        Add(p.ContainingType, p.IsStatic);

                    if (model.GetSymbolInfo(ma.Expression).Symbol is INamedTypeSymbol nt)
                        Add(nt, staticUse: true);
                    break;

                case VariableDeclarationSyntax vd:
                    Add(model.GetTypeInfo(vd.Type).Type);
                    break;
            }
        }

        return seen.Values
            .OrderBy(t => t.Namespace.StartsWith("System", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------
    //  Bewertung
    // ------------------------------------------------------------------

    private static string EvaluateTestability(MethodFact method, List<TypeFact> types)
    {
        var blockers = new List<string>();

        if (method.Accessibility is "Private" or "Protected")
            blockers.Add($"Methode ist '{method.Accessibility}' und kann nicht direkt aufgerufen werden.");

        if (method.IsAsyncVoid)
            blockers.Add("Methode ist 'async void' und kann nicht awaited werden.");

        if (method.IsStatic)
            blockers.Add("Methode ist 'static' und schlecht isolierbar.");

        foreach (var t in types.Where(t => t.UsedStatically))
            blockers.Add($"Statische Abhängigkeit auf '{t.FullName}' – nicht mockbar.");

        return blockers.Count == 0
            ? "Direkt testbar."
            : "NICHT direkt testbar: " + string.Join(" | ", blockers);
    }

    private static List<string> BuildRecommendations(MethodFact method, List<TypeFact> types)
    {
        var recs = new List<string>();

        if (method.Accessibility is "Private" or "Protected")
            recs.Add("Sichtbarkeit auf 'internal' setzen + InternalsVisibleTo, oder Logik in eine separate Klasse verschieben.");

        if (method.IsAsyncVoid)
            recs.Add("Event-Handler auf 'async Task' umstellen; XAML-Handler wird dünner Wrapper.");

        var statics = types.Where(t => t.UsedStatically && !t.IsInterface).ToList();
        if (statics.Count > 0)
            recs.Add("Statische Abhängigkeiten hinter Interfaces legen (" +
                     string.Join(", ", statics.Select(t => t.FullName)) +
                     ") – z. B. via System.IO.Abstractions für File/Directory.");

        var concrete = types
            .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsStatic && !t.IsSealed
                        && !t.Namespace.StartsWith("System", StringComparison.Ordinal))
            .ToList();
        if (concrete.Count > 0)
            recs.Add("Konkrete Typen werden intern erzeugt – besser per Konstruktor injizieren: " +
                     string.Join(", ", concrete.Select(t => t.FullName)));

        return recs;
    }

    // ------------------------------------------------------------------
    //  Hilfsfunktionen
    // ------------------------------------------------------------------

    private static string KindOf(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Class => "Class",
        TypeKind.Interface => "Interface",
        TypeKind.Struct => "Struct",
        TypeKind.Enum => "Enum",
        TypeKind.Delegate => "Delegate",
        _ => type.TypeKind.ToString()
    };

    private static string ClassMockability(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface) return "Ja (Interface)";
        if (type.IsStatic) return "Nein (static)";
        if (type.IsSealed) return "Nein (sealed)";
        if (type.IsAbstract) return "Ja (abstract)";

        var virtualCount = type.GetMembers()
            .Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride);

        return virtualCount == 0
            ? "Nein (keine virtuellen Member)"
            : "Eingeschränkt (virtuelle Member vorhanden)";
    }

    private static Document? FindDocument(Solution solution, string name) =>
        solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => Matches(d, name));

    private static Document? FindDocument(Project project, string name) =>
        project.Documents.FirstOrDefault(d => Matches(d, name));

    private static bool Matches(Document d, string name) =>
        string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) ||
        d.FilePath?.EndsWith(name, StringComparison.OrdinalIgnoreCase) == true ||
        d.FilePath?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;
}