//using Microsoft.CodeAnalysis;
//using Microsoft.CodeAnalysis.CSharp;
//using Microsoft.CodeAnalysis.CSharp.Syntax;
//using NetAI.TestGenerator.Core.Models;


//#if !NETSTANDARD2_0
//using Microsoft.CodeAnalysis.MSBuild;
//using Microsoft.Build.Locator;
//#endif

//namespace NetAI.TestGenerator.Core.Models;

///// <summary>Analyzes testability using Roslyn workspaces loaded from a solution, project, or document.</summary>
//public sealed class RoslynTestabilityAnalyzer
//{
//    private static readonly SymbolDisplayFormat FqFormat = SymbolDisplayFormat.FullyQualifiedFormat
//        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

//    private readonly AnalyzerOptions _options;

//    /// <summary>Creates an analyzer with optional report and framework-type settings.</summary>
//    /// <param name="options">Analysis options, or <see langword="null"/> to use defaults.</param>
//    public RoslynTestabilityAnalyzer(AnalyzerOptions? options = null) => _options = options ?? new AnalyzerOptions();

//#if !NETSTANDARD2_0
//    /// <summary>Loads a solution and analyzes a named method in the requested document.</summary>
//    /// <param name="solutionPath">Path to the solution file.</param>
//    /// <param name="documentName">Document name or path fragment to locate.</param>
//    /// <param name="methodName">Name of the method to analyze.</param>
//    /// <param name="ct">Token used to cancel workspace loading or analysis.</param>
//    /// <returns>A report describing the method and its testability.</returns>
//    public async Task<TestabilityReport> AnalyzeFromSolutionAsync(
//        string solutionPath, string documentName, string methodName, CancellationToken ct = default)
//    {
//        if (!MSBuildLocator.IsRegistered)
//        {
//            MSBuildLocator.RegisterDefaults();
//        }

//        using var workspace = MSBuildWorkspace.Create();
//        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
//        var document = FindDocument(solution, documentName) ?? throw new InvalidOperationException(
//            $"Document '{documentName}' was not found in the solution.");

//        return await AnalyzeDocumentAsync(document, methodName, ct);
//    }

//    /// <summary>Loads a project and analyzes a named method in the requested document.</summary>
//    /// <param name="projectPath">Path to the project file.</param>
//    /// <param name="documentName">Document name or path fragment to locate.</param>
//    /// <param name="methodName">Name of the method to analyze.</param>
//    /// <param name="ct">Token used to cancel workspace loading or analysis.</param>
//    /// <returns>A report describing the method and its testability.</returns>
//    public async Task<TestabilityReport> AnalyzeFromProjectAsync(
//        string projectPath, string documentName, string methodName, CancellationToken ct = default)
//    {
//        if (!MSBuildLocator.IsRegistered)
//        {
//            MSBuildLocator.RegisterDefaults();
//        }

//        using var workspace = MSBuildWorkspace.Create();
//        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
//        var document = FindDocument(project, documentName) ?? throw new InvalidOperationException(
//            $"Document '{documentName}' was not found in the project.");

//        return await AnalyzeDocumentAsync(document, methodName, ct);
//    }
//#endif

//    /// <summary>Analyzes a method in an already loaded Roslyn document.</summary>
//    /// <param name="document">Document containing the method to analyze.</param>
//    /// <param name="methodName">Name of the method to analyze.</param>
//    /// <param name="ct">Token used to cancel the analysis.</param>
//    /// <returns>A report describing the method and its testability.</returns>
//    public async Task<TestabilityReport> AnalyzeDocumentAsync(
//        Document document,
//        string methodName,
//        CancellationToken ct = default)
//    {
//        var compilation = await document.Project.GetCompilationAsync(ct)
//            ?? throw new InvalidOperationException("Compilation could not be created.");
//        var tree = await document.GetSyntaxTreeAsync(ct)
//            ?? throw new InvalidOperationException("Syntax tree is missing.");
//        var model = compilation.GetSemanticModel(tree);

//        var methodDecl = tree.GetRoot(ct)
//            .DescendantNodes()
//            .OfType<MethodDeclarationSyntax>()
//            .FirstOrDefault(m => m.Identifier.Text == methodName)
//            ?? throw new InvalidOperationException(
//                $"Method '{methodName}' was not found in the document.");

//        var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol
//                           ?? throw new InvalidOperationException("No method symbol was found.");

//        return BuildReport(document, methodSymbol, methodDecl, model, compilation);
//    }

//    private TestabilityReport BuildReport(
//        Document document,
//        IMethodSymbol method,
//        MethodDeclarationSyntax syntax,
//        SemanticModel model,
//        Compilation compilation)
//    {
//        var errors = compilation.GetDiagnostics()
//            .Where(d => d.Severity == DiagnosticSeverity.Error)
//            .Take(_options.MaxDiagnostics)
//            .Select(d => d.ToString())
//            .ToList();

//        var methodFact = BuildMethodFact(method);
//        var typeFacts = CollectReferencedTypes(syntax, model);
//        var verdict = EvaluateTestability(methodFact, typeFacts);
//        var recs = BuildRecommendations(methodFact, typeFacts);

//        return new TestabilityReport
//        {
//            GeneratedAt = DateTimeOffset.UtcNow,
//            DocumentName = document.Name,
//            Method = methodFact,
//            ReferencedTypes = typeFacts,
//            Verdict = verdict,
//            Recommendations = recs,
//            CompilationErrors = errors
//        };
//    }

//    private static MethodFact BuildMethodFact(IMethodSymbol method)
//    {
//        var containing = method.ContainingType;

//        return new MethodFact
//        {
//            Name = method.Name,
//            Signature = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
//            ReturnType = method.ReturnType.ToDisplayString(FqFormat),
//            Accessibility = method.DeclaredAccessibility.ToString(),
//            IsStatic = method.IsStatic,
//            IsAsync = method.IsAsync,
//            ReturnsVoid = method.ReturnsVoid,
//            IsAsyncVoid = method.IsAsync && method.ReturnsVoid,
//            ContainingType = new TypeFact
//            {
//                FullName = containing.ToDisplayString(FqFormat),
//                Namespace = containing.ContainingNamespace?.ToDisplayString() ?? "",
//                Kind = KindOf(containing),
//                Accessibility = containing.DeclaredAccessibility.ToString(),
//                IsStatic = containing.IsStatic,
//                IsSealed = containing.IsSealed,
//                IsAbstract = containing.IsAbstract,
//                IsInterface = containing.TypeKind == TypeKind.Interface,
//                Mockable = ClassMockability(containing),
//                UsedStatically = false,
//                Interfaces = containing.AllInterfaces.Select(i => i.ToDisplayString(FqFormat)).ToList(),
//                Constructors = containing.InstanceConstructors
//                    .Where(c => c.DeclaredAccessibility == Accessibility.Public)
//                    .Select(c => c.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
//                    .ToList()
//            },
//            Parameters = method.Parameters.Select(p => new ParameterFact
//            {
//                Name = p.Name,
//                Type = p.Type.ToDisplayString(FqFormat),
//                IsOptional = p.IsOptional,
//                HasDefaultValue = p.HasExplicitDefaultValue,
//                DefaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
//            }).ToList()
//        };
//    }

//    private List<TypeFact> CollectReferencedTypes(
//        MethodDeclarationSyntax syntax,
//        SemanticModel model)
//    {
//        var seen = new Dictionary<string, TypeFact>(StringComparer.Ordinal);

//        void Add(ITypeSymbol? symbol, bool staticUse = false)
//        {
//            if (symbol is null) return;
//            if (symbol is ITypeParameterSymbol) return;
//            if (symbol.SpecialType != SpecialType.None) return;

//            if (symbol is not INamedTypeSymbol named) return;

//            if (named.IsGenericType)
//            {
//                foreach (var arg in named.TypeArguments)
//                    Add(arg, staticUse);
//            }

//            var full = named.OriginalDefinition.ToDisplayString(FqFormat);
//            if (full.StartsWith("System.Nullable", StringComparison.Ordinal)) return;

//            var isFramework = named.ContainingNamespace?.ToDisplayString()
//                .StartsWith("System", StringComparison.Ordinal) == true;

//            if (isFramework && !_options.IncludeFrameworkTypes && !staticUse)
//                return;

//            if (seen.TryGetValue(full, out var existing))
//            {
//                if (staticUse) existing.UsedStatically = true;
//                return;
//            }

//            var fact = new TypeFact
//            {
//                FullName = full,
//                Namespace = named.ContainingNamespace?.ToDisplayString() ?? "",
//                Kind = KindOf(named),
//                Accessibility = named.DeclaredAccessibility.ToString(),
//                IsStatic = named.IsStatic,
//                IsSealed = named.IsSealed,
//                IsAbstract = named.IsAbstract,
//                IsInterface = named.TypeKind == TypeKind.Interface,
//                Mockable = ClassMockability(named),
//                UsedStatically = staticUse,
//                Constructors = named.InstanceConstructors
//                    .Where(c => c.DeclaredAccessibility == Accessibility.Public)
//                    .Select(c => c.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
//                    .ToList(),
//                Interfaces = named.AllInterfaces.Select(i => i.ToDisplayString(FqFormat)).ToList(),
//                VirtualMemberCount = named.GetMembers().Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride),
//                MemberCount = named.GetMembers().Length
//            };

//            seen[full] = fact;
//        }

//        foreach (var node in syntax.DescendantNodes())
//        {
//            switch (node)
//            {
//                case ObjectCreationExpressionSyntax oc:
//                    Add(model.GetTypeInfo(oc).Type);
//                    break;

//                case InvocationExpressionSyntax inv:
//                    if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol mi)
//                    {
//                        Add(mi.ContainingType, mi.IsStatic);
//                        Add(mi.ReturnType, mi.IsStatic);
//                    }
//                    break;

//                case MemberAccessExpressionSyntax ma:
//                    if (model.GetSymbolInfo(ma).Symbol is IMethodSymbol m)
//                        Add(m.ContainingType, m.IsStatic);
//                    else if (model.GetSymbolInfo(ma).Symbol is IPropertySymbol p)
//                        Add(p.ContainingType, p.IsStatic);

//                    if (model.GetSymbolInfo(ma.Expression).Symbol is INamedTypeSymbol nt)
//                        Add(nt, staticUse: true);
//                    break;

//                case VariableDeclarationSyntax vd:
//                    Add(model.GetTypeInfo(vd.Type).Type);
//                    break;
//            }
//        }

//        return seen.Values
//            .OrderBy(t => t.Namespace.StartsWith("System", StringComparison.Ordinal) ? 1 : 0)
//            .ThenBy(t => t.FullName, StringComparer.Ordinal)
//            .ToList();
//    }

//    private static string EvaluateTestability(MethodFact method, List<TypeFact> types)
//    {
//        var blockers = new List<string>();

//        if (method.Accessibility is "Private" or "Protected")
//            blockers.Add($"Method is '{method.Accessibility}' and cannot be called directly.");

//        if (method.IsAsyncVoid)
//            blockers.Add("Method is 'async void' and cannot be awaited.");

//        if (method.IsStatic)
//            blockers.Add("Method is 'static' and difficult to isolate.");

//        foreach (var t in types.Where(t => t.UsedStatically))
//            blockers.Add($"Static dependency on '{t.FullName}' - cannot be mocked.");

//        return blockers.Count == 0
//            ? "Directly testable."
//            : "NOT directly testable: " + string.Join(" | ", blockers);
//    }

//    private static List<string> BuildRecommendations(MethodFact method, List<TypeFact> types)
//    {
//        var recs = new List<string>();

//        if (method.Accessibility is "Private" or "Protected")
//            recs.Add("Set accessibility to 'internal' and add InternalsVisibleTo, or move the logic into a separate class.");

//        if (method.IsAsyncVoid)
//            recs.Add("Change the event handler to 'async Task'; keep the XAML handler as a thin wrapper.");

//        var statics = types.Where(t => t.UsedStatically && !t.IsInterface).ToList();
//        if (statics.Count > 0)
//            recs.Add("Put static dependencies behind interfaces (" +
//                     string.Join(", ", statics.Select(t => t.FullName)) +
//                     "), e.g. use System.IO.Abstractions for File/Directory.");

//        var concrete = types
//            .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsStatic && !t.IsSealed
//                        && !t.Namespace.StartsWith("System", StringComparison.Ordinal))
//            .ToList();
//        if (concrete.Count > 0)
//            recs.Add("Concrete types are created internally; prefer constructor injection: " +
//                     string.Join(", ", concrete.Select(t => t.FullName)));

//        return recs;
//    }

//    private static string KindOf(INamedTypeSymbol type) => type.TypeKind switch
//    {
//        TypeKind.Class => "Class",
//        TypeKind.Interface => "Interface",
//        TypeKind.Struct => "Struct",
//        TypeKind.Enum => "Enum",
//        TypeKind.Delegate => "Delegate",
//        _ => type.TypeKind.ToString()
//    };

//    private static string ClassMockability(INamedTypeSymbol type)
//    {
//        if (type.TypeKind == TypeKind.Interface) return "Yes (interface)";
//        if (type.IsStatic) return "No (static)";
//        if (type.IsSealed) return "No (sealed)";
//        if (type.IsAbstract) return "Yes (abstract)";

//        var virtualCount = type.GetMembers()
//            .Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride);

//        return virtualCount == 0
//            ? "No (no virtual members)"
//            : "Limited (virtual members exist)";
//    }

//    private static Document? FindDocument(Solution solution, string name) =>
//        solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => Matches(d, name));

//    private static Document? FindDocument(Project project, string name) =>
//        project.Documents.FirstOrDefault(d => Matches(d, name));

//    private static bool Matches(Document d, string name) =>
//        string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) ||
//        d.FilePath?.EndsWith(name, StringComparison.OrdinalIgnoreCase) == true ||
//        d.FilePath?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;
//}