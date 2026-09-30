using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetAI.TestGenerator.Core.Analysis
{
    public sealed class RoslynDllTestabilityAnalyzer
    {
        private static readonly SymbolDisplayFormat FqFormat =
            SymbolDisplayFormat.FullyQualifiedFormat
                .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

        private readonly AnalyzerOptions _options;

        public RoslynDllTestabilityAnalyzer(AnalyzerOptions? options = null)
            => _options = options ?? new AnalyzerOptions();

        public Task<TestabilityReport> AnalyzeFromSourceFilesAsync(
            IEnumerable<string> sourceFilePaths,
            IEnumerable<string> referenceDllPaths,
            string methodName,
            string? documentName = null,
            CancellationToken ct = default)
        {
            var compilation = BuildCompilation(sourceFilePaths, referenceDllPaths);

            return AnalyzeFromCompilationAsync(
                compilation, methodName, documentName, ct);
        }

        public Task<TestabilityReport> AnalyzeFromDirectoryAsync(
            string directory,
            IEnumerable<string> referenceDllPaths,
            string methodName,
            string? documentName = null,
            string searchPattern = "*.cs",
            bool recursive = true,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Verzeichnis darf nicht leer sein.", nameof(directory));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(directory, searchPattern, option)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .ToList();

            return AnalyzeFromSourceFilesAsync(
                files, referenceDllPaths, methodName, documentName, ct);
        }

        public Task<TestabilityReport> AnalyzeFromCompilationAsync(
            Compilation compilation,
            string methodName,
            string? documentName = null,
            CancellationToken ct = default)
        {
            if (compilation is null) throw new ArgumentNullException(nameof(compilation));

            var (tree, methodDecl) = FindMethodAcrossTrees(compilation, methodName, documentName, ct)
                                     ?? throw new InvalidOperationException(
                                         $"Methode '{methodName}' wurde in keiner Quelldatei gefunden.");

            var model = compilation.GetSemanticModel(tree);

            var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol
                               ?? throw new InvalidOperationException("Kein Methodensymbol gefunden.");

            var resolvedDocumentName = ResolveDocumentName(documentName, tree);

            var report = BuildReport(
                resolvedDocumentName, methodSymbol, methodDecl, model, compilation);

            return Task.FromResult(report);
        }

        private static string ResolveDocumentName(string? requestedName, SyntaxTree tree)
        {
            if (!string.IsNullOrWhiteSpace(requestedName))
                return requestedName!;

            var fromPath = Path.GetFileName(tree.FilePath);
            if (!string.IsNullOrEmpty(fromPath))
                return fromPath;

            return "(unbenannt)";
        }

        public static CSharpCompilation BuildCompilation(
            IEnumerable<string> sourceFilePaths,
            IEnumerable<string> referenceDllPaths,
            string assemblyName = "TestabilityAnalysis")
        {
            if (sourceFilePaths is null) throw new ArgumentNullException(nameof(sourceFilePaths));

            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);

            var trees = new List<SyntaxTree>();
            foreach (var path in sourceFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                try
                {
                    var text = File.ReadAllText(path);
                    trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: path));
                }
                catch
                {
                }
            }

            var references = new List<MetadataReference>();
            if (referenceDllPaths != null)
            {
                foreach (var dll in referenceDllPaths)
                {
                    if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) continue;

                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(dll));
                    }
                    catch
                    {
                    }
                }
            }

            return CSharpCompilation.Create(
                assemblyName,
                syntaxTrees: trees,
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug));
        }

        private TestabilityReport BuildReport(
            string documentName,
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
                DocumentName = documentName,
                Method = methodFact,
                ReferencedTypes = typeFacts,
                Verdict = verdict,
                Recommendations = recs,
                CompilationErrors = errors,
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
                    Interfaces = containing.AllInterfaces
                        .Select(i => i.ToDisplayString(FqFormat)).ToList(),
                    Constructors = containing.InstanceConstructors
                        .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                        .Select(c => c.ToDisplayString(
                            SymbolDisplayFormat.MinimallyQualifiedFormat))
                        .ToList(),
                },
                Parameters = method.Parameters.Select(p => new ParameterFact
                {
                    Name = p.Name,
                    Type = p.Type.ToDisplayString(FqFormat),
                    IsOptional = p.IsOptional,
                    HasDefaultValue = p.HasExplicitDefaultValue,
                    DefaultValue = p.HasExplicitDefaultValue
                        ? p.ExplicitDefaultValue?.ToString()
                        : null,
                }).ToList(),
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
                if (symbol.SpecialType != SpecialType.None) return;
                if (!(symbol is INamedTypeSymbol named)) return;

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

                seen[full] = new TypeFact
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
                        .Select(c => c.ToDisplayString(
                            SymbolDisplayFormat.MinimallyQualifiedFormat))
                        .ToList(),
                    Interfaces = named.AllInterfaces
                        .Select(i => i.ToDisplayString(FqFormat)).ToList(),
                    VirtualMemberCount = named.GetMembers()
                        .Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride),
                    MemberCount = named.GetMembers().Length,
                };
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
                recs.Add("Sichtbarkeit auf 'internal' setzen + InternalsVisibleTo, " +
                         "oder Logik in eine separate Klasse verschieben.");

            if (method.IsAsyncVoid)
                recs.Add("Event-Handler auf 'async Task' umstellen; " +
                         "XAML-Handler wird dünner Wrapper.");

            var statics = types.Where(t => t.UsedStatically && !t.IsInterface).ToList();
            if (statics.Count > 0)
            {
                recs.Add("Statische Abhängigkeiten hinter Interfaces legen (" +
                         string.Join(", ", statics.Select(t => t.FullName)) +
                         ") – z. B. via System.IO.Abstractions für File/Directory.");
            }

            var concrete = types
                .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsStatic && !t.IsSealed
                            && !t.Namespace.StartsWith("System", StringComparison.Ordinal))
                .ToList();

            if (concrete.Count > 0)
            {
                recs.Add("Konkrete Typen werden intern erzeugt – besser per Konstruktor " +
                         "injizieren: " + string.Join(", ", concrete.Select(t => t.FullName)));
            }

            return recs;
        }

        private static string KindOf(INamedTypeSymbol type) => type.TypeKind switch
        {
            TypeKind.Class => "Class",
            TypeKind.Interface => "Interface",
            TypeKind.Struct => "Struct",
            TypeKind.Enum => "Enum",
            TypeKind.Delegate => "Delegate",
            _ => type.TypeKind.ToString(),
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

        private static (SyntaxTree Tree, MethodDeclarationSyntax Method)? FindMethodAcrossTrees(
            Compilation compilation,
            string methodName,
            string? documentName,
            CancellationToken ct)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(documentName))
                {
                    var file = Path.GetFileName(tree.FilePath);
                    if (!string.Equals(file, documentName, StringComparison.OrdinalIgnoreCase) &&
                        tree.FilePath?.Contains(documentName!, StringComparison.OrdinalIgnoreCase) != true)
                    {
                        continue;
                    }
                }

                var root = tree.GetRoot(ct);
                var method = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == methodName);

                if (method != null)
                    return (tree, method);
            }

            return null;
        }
    }
}