using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models;
using NetAI.TestGenerator.Core.Models.Enums;

#if !NETSTANDARD2_0
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
#endif

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>
///     Roslyn testability analyzer, tuned for small local LLMs (Qwen 2.5 14B etc.).
///     Precision intentionally sacrificed: the report only carries the facts that the
///     prompt-builder actually needs to emit a correct [Fact]/[StaFact]/Skip test.
/// </summary>
public sealed class RoslynDllTestabilityAnalyzer
{
    private static readonly SymbolDisplayFormat FqFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>
    ///     Only these static APIs are treated as genuine blockers.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Abstraction, string? Package)> StaticApiAbstractions =
        new Dictionary<string, (string, string?)>(StringComparer.Ordinal)
        {
            ["System.IO.File"] = ("IFileSystem", "System.IO.Abstractions"),
            ["System.IO.Directory"] = ("IFileSystem", "System.IO.Abstractions"),
            ["System.IO.Path"] = ("IPath", "System.IO.Abstractions"),
            ["System.IO.FileStream"] = ("IFileSystem", "System.IO.Abstractions"),
            ["System.DateTime"] = ("TimeProvider", null),
            ["System.DateTimeOffset"] = ("TimeProvider", null),
            ["System.Guid"] = ("IGuidProvider", null),
            ["System.Random"] = ("IRandom", null),
            ["System.Environment"] = ("IEnvironment", null),
            ["System.Console"] = ("IConsole", null),
            ["System.Net.Http.HttpClient"] = ("IHttpClientFactory", "Microsoft.Extensions.Http"),
            ["System.Threading.Thread"] = ("Task / TimeProvider", null),
            ["System.Diagnostics.Process"] = ("IProcessRunner", null),
            ["System.IO.Compression.ZipFile"] = ("IZipService", null),
            ["System.AppDomain"] = ("IAppEnvironment", null)
        };

    /// <summary>
    ///     Bekannte WPF-Basistypen. Wenn einer davon in der Vererbungskette
    ///     der Klasse unter Test auftaucht, braucht der Test einen STA-Thread.
    /// </summary>
    private static readonly HashSet<string> WpfBaseTypes = new(StringComparer.Ordinal)
    {
        "System.Windows.Window",
        "System.Windows.Controls.UserControl",
        "System.Windows.Controls.Page",
        "System.Windows.Controls.Control",
        "System.Windows.Controls.ContentControl",
        "System.Windows.Controls.ItemsControl",
        "System.Windows.FrameworkElement",
        "System.Windows.UIElement",
        "System.Windows.Media.Visual",
        "System.Windows.Media.Media3D.Visual3D",
        "System.Windows.DependencyObject",
        "System.Windows.Threading.DispatcherObject",
        "System.Windows.Application"
    };

    /// <summary>
    ///     Default-Profil, falls <see cref="AnalyzerOptions.TestFrameworkProfile" />
    ///     nicht gesetzt ist. Spiegelt das Verhalten vor der Profil-Einführung.
    /// </summary>
    private static readonly TestFrameworkProfile DefaultProfile =
        TestFrameworkProfile.Create(
            TestFramework.xUnit,
            MockFramework.Moq,
            useFluent: true,
            useAutoFixture: false);

    private readonly AnalyzerOptions _options;

    public RoslynDllTestabilityAnalyzer(AnalyzerOptions? options = null)
    {
        _options = options ?? new AnalyzerOptions();
    }

    // ---------------------------------------------------------------- public API

    public Task<TestabilityReport> AnalyzeFromSourceFilesAsync(IEnumerable<string> sourceFilePaths, IEnumerable<string> referenceDllPaths,
        string methodName, string? documentName = null, CancellationToken ct = default)
    {
        var compilation = BuildCompilation(sourceFilePaths, referenceDllPaths);
        return AnalyzeFromCompilationAsync(compilation, methodName, documentName, ct);
    }

    public Task<TestabilityReport> AnalyzeFromDirectoryAsync(string directory, IEnumerable<string> referenceDllPaths, string methodName,
        string? documentName = null, string searchPattern = "*.cs", bool recursive = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory must not be empty.", nameof(directory));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(directory, searchPattern, option)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();

        return AnalyzeFromSourceFilesAsync(files, referenceDllPaths, methodName, documentName, ct);
    }

    public Task<TestabilityReport> AnalyzeFromCompilationAsync(Compilation compilation, string methodName, string? documentName = null,
        CancellationToken ct = default)
    {
        if (compilation is null) throw new ArgumentNullException(nameof(compilation));

        var result = FindMethodAcrossTrees(compilation, methodName, documentName, ct);
        if (result is null)
        {
            if (!string.IsNullOrWhiteSpace(documentName) &&
                !compilation.SyntaxTrees.Any(t => MatchesDocumentPath(t.FilePath, documentName!)))
            {
                var available = compilation.SyntaxTrees
                    .Select(t => Path.GetFileName(t.FilePath))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

                throw new InvalidOperationException(
                    $"Document '{documentName}' was not found in the compilation. " +
                    $"Available documents: {string.Join(", ", available)}");
            }

            throw new InvalidOperationException($"Method '{methodName}' was not found in any source file.");
        }

        var (tree, methodDecl) = result.Value;
        var model = compilation.GetSemanticModel(tree);
        var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct)
            ?? throw new InvalidOperationException("No method symbol was found.");

        var documentNameResolved = ResolveDocumentName(documentName, tree);
        var report = BuildReport(documentNameResolved, methodSymbol, methodDecl, model, compilation);
        return Task.FromResult(report);
    }

    public Task<TestabilityReport> AnalyzeFromCompilationAsync(Compilation compilation, MethodDeclarationSyntax methodDeclaration,
        CancellationToken ct = default)
    {
        if (compilation is null) throw new ArgumentNullException(nameof(compilation));
        if (methodDeclaration is null) throw new ArgumentNullException(nameof(methodDeclaration));

        var (tree, localDecl) = FindEquivalentDeclaration(compilation, methodDeclaration, ct)
            ?? throw new InvalidOperationException(
                $"Method '{methodDeclaration.Identifier.Text}' was not found in the compilation.");

        var model = compilation.GetSemanticModel(tree);
        var methodSymbol = model.GetDeclaredSymbol(localDecl, ct)
            ?? throw new InvalidOperationException("No method symbol was found.");

        var report = BuildReport(ResolveDocumentName(null, tree), methodSymbol, localDecl, model, compilation);
        return Task.FromResult(report);
    }

    // ---------------------------------------------------------------- MSBuild loading

#if !NETSTANDARD2_0
    public async Task<TestabilityReport> AnalyzeFromSolutionAsync(
        string solutionPath, string documentName, string methodName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(solutionPath)) throw new ArgumentException("Solution path must not be empty.", nameof(solutionPath));
        if (!File.Exists(solutionPath)) throw new FileNotFoundException("Solution file not found.", solutionPath);

        EnsureMSBuildRegistered();

       var workspace = CreateMsBuildWorkspace();

        workspace.SkipUnrecognizedProjects = true;
        workspace.WorkspaceFailed += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[MSBuildWorkspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct).ConfigureAwait(false);

        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => MatchesDocument(d, documentName))
            ?? throw new InvalidOperationException(
                $"Document '{documentName}' was not found in solution '{Path.GetFileName(solutionPath)}'.");

        return await AnalyzeDocumentAsync(document, methodName, ct).ConfigureAwait(false);
    }

    public async Task<TestabilityReport> AnalyzeFromProjectAsync(
        string projectPath, string documentName, string methodName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("Project path must not be empty.", nameof(projectPath));
        if (!File.Exists(projectPath)) throw new FileNotFoundException("Project file not found.", projectPath);

        EnsureMSBuildRegistered();

var workspace = CreateMsBuildWorkspace();

        workspace.SkipUnrecognizedProjects = true;
        workspace.WorkspaceFailed += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[MSBuildWorkspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");

        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct).ConfigureAwait(false);

        var document = project.Documents.FirstOrDefault(d => MatchesDocument(d, documentName))
            ?? throw new InvalidOperationException(
                $"Document '{documentName}' was not found in project '{Path.GetFileName(projectPath)}'.");

        return await AnalyzeDocumentAsync(document, methodName, ct).ConfigureAwait(false);
    }

    public async Task<TestabilityReport> AnalyzeDocumentAsync(Document document, string methodName, CancellationToken ct = default)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("Method name must not be empty.", nameof(methodName));

        var compilation = await document.Project.GetCompilationAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Compilation could not be created for the document's project.");
        var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Syntax tree is missing for the document.");

        var model = compilation.GetSemanticModel(tree);

        var methodDecl = tree.GetRoot(ct).DescendantNodes().OfType<MethodDeclarationSyntax>()
                             .FirstOrDefault(m => m.Identifier.Text == methodName)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found in document '{document.Name}'.");

        var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol
            ?? throw new InvalidOperationException("No method symbol was found.");

        return BuildReport(document.Name, methodSymbol, methodDecl, model, compilation);
    }

    public async Task<(IDisposable Workspace, Compilation? Compilation)> LoadCompilationFromMsbuildAsync(
        string? solutionPath, string? projectPath, string documentName,
        Action<string>? logInfo = null, CancellationToken ct = default)
    {
        EnsureMSBuildRegistered();

       var workspace = CreateMsBuildWorkspace();

        workspace.SkipUnrecognizedProjects = true;
        workspace.WorkspaceFailed += (_, e) =>
            logInfo?.Invoke($"[MSBuildWorkspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");

        Document? document = null;

        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct).ConfigureAwait(false);
            document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => MatchesDocument(d, documentName));
        }
        else if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct).ConfigureAwait(false);
            document = project.Documents.FirstOrDefault(d => MatchesDocument(d, documentName));
        }

        if (document == null)
        {
            workspace.Dispose();
            return (new NoopDisposable(), null);
        }

        var compilation = await document.Project.GetCompilationAsync(ct).ConfigureAwait(false);
        return (workspace, compilation);
    }

    private static void EnsureMSBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }

    private static bool MatchesDocument(Document document, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(document.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return MatchesDocumentPath(document.FilePath, name);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
#endif

    // ---------------------------------------------------------------- lookup helpers

    private static (SyntaxTree Tree, MethodDeclarationSyntax Method)? FindEquivalentDeclaration(Compilation compilation,
        MethodDeclarationSyntax external, CancellationToken ct)
    {
        var methodName = external.Identifier.Text;
        var paramCount = external.ParameterList.Parameters.Count;
        var externalPath = external.SyntaxTree?.FilePath;
        var externalFileName = string.IsNullOrEmpty(externalPath) ? null : Path.GetFileName(externalPath);

        if (!string.IsNullOrEmpty(externalPath) || !string.IsNullOrEmpty(externalFileName))
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                var treePath = tree.FilePath;
                if (string.IsNullOrEmpty(treePath)) continue;

                var sameFullPath = !string.IsNullOrEmpty(externalPath) &&
                                   string.Equals(treePath, externalPath, StringComparison.OrdinalIgnoreCase);
                var sameFileName = !string.IsNullOrEmpty(externalFileName) &&
                                   string.Equals(Path.GetFileName(treePath), externalFileName, StringComparison.OrdinalIgnoreCase);

                if (!sameFullPath && !sameFileName) continue;

                var match = FindMethodInTree(tree, methodName, paramCount, ct);
                if (match != null) return (tree, match);
            }
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            var match = FindMethodInTree(tree, methodName, paramCount, ct);
            if (match != null) return (tree, match);
        }

        return null;
    }

    private static MethodDeclarationSyntax? FindMethodInTree(SyntaxTree tree, string methodName, int paramCount, CancellationToken ct)
    {
        var root = tree.GetRoot(ct);
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName && m.ParameterList.Parameters.Count == paramCount);
    }

    // ---------------------------------------------------------------- compilation

    public static CSharpCompilation BuildCompilation(IEnumerable<string> sourceFilePaths, IEnumerable<string> referenceDllPaths,
        string assemblyName = "TestabilityAnalysis", CSharpParseOptions? parseOptions = null,
        CSharpCompilationOptions? compilationOptions = null)
    {
        if (sourceFilePaths is null) throw new ArgumentNullException(nameof(sourceFilePaths));

        parseOptions ??= new CSharpParseOptions(LanguageVersion.Latest);

        var trees = new List<SyntaxTree>();
        foreach (var path in sourceFilePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path));
            }
            catch { /* ignore */ }
        }

        var references = new List<MetadataReference>();
        if (referenceDllPaths != null)
        {
            foreach (var dll in referenceDllPaths)
            {
                if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) continue;
                try { references.Add(MetadataReference.CreateFromFile(dll)); } catch { /* ignore */ }
            }
        }

        return CSharpCompilation.Create(assemblyName, trees, references,
            compilationOptions ?? new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));
    }

    // ---------------------------------------------------------------- report assembly

    private TestabilityReport BuildReport(string documentName, IMethodSymbol method, MethodDeclarationSyntax syntax,
    SemanticModel model, Compilation compilation)
    {
        // Compiler-Diagnosen
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(_options.MaxDiagnostics)
            .Select(d => d.ToString())
            .ToList();

        var methodFact = BuildMethodFact(method);
        var (typeFacts, instanceFieldTypes) = CollectReferencedTypes(method, compilation);
        var verdict = EvaluateTestability(methodFact, typeFacts, instanceFieldTypes);
        var recs = BuildRecommendations(methodFact, typeFacts, instanceFieldTypes);

        var callGraph = new List<string> { methodFact.Signature };

        // STA-Bedarf
        var requiresSta = DetectStaRequirement(method.ContainingType);

        // NEU: Strategie bestimmen - dieselbe Logik, die auch der Orchestrator
        // im XML verwendet, damit Skelett und <SuggestedTestStrategy> konsistent sind.
        var strategy = DetermineStrategy(methodFact, verdict.IsDirectlyTestable, verdict.Blockers);

        // NEU: Skip-Grund und Refactoring-Zeilen für Skip-/RefactorFirst-Skelette
        var skipReason = strategy == UnitTestSkeletonGenerator.TestStrategy.RefactorFirst
            ? BuildRefactorFirstReason(methodFact)
            : null;

        // Test-Skelett erzeugen
        var profile = _options.TestFrameworkProfile ?? DefaultProfile;

        UnitTestSkeletonGenerator.GeneratorResult? skeleton = null;
        try
        {
            skeleton = UnitTestSkeletonGenerator.GenerateFromMethod(
                method, profile, requiresSta, strategy,
                recs.SourceRefactoring, skipReason);
        }
        catch (Exception ex)
        {
            errors.Add($"// Skeleton generation failed: {ex.Message}");
        }

        return new TestabilityReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            DocumentName = documentName,
            Method = methodFact,
            ReferencedTypes = typeFacts,
            Verdict = verdict.Text,
            IsDirectlyTestable = verdict.IsDirectlyTestable,
            Blockers = verdict.Blockers,
            Recommendations = recs.SourceRefactoring.Concat(recs.TestStrategy).Distinct().ToList(),
            SourceRefactoringRecommendations = recs.SourceRefactoring,
            TestStrategyRecommendations = recs.TestStrategy,
            CompilationErrors = errors,
            AnalyzedCallGraph = callGraph,

            TestSkeleton = skeleton,
            RequiresSta = requiresSta,
            Strategy = strategy
        };
    }

    /// <summary>
    ///     Spiegelt exakt die Logik, die der Orchestrator in <c>AppendSuggestedTestStrategy</c>
    ///     verwendet, damit Skeleton-Mode und SuggestedTestStrategy-XML konsistent bleiben.
    /// </summary>
    private static UnitTestSkeletonGenerator.TestStrategy DetermineStrategy(
        MethodFact method, bool isDirectlyTestable, List<string> blockers)
    {
        //// Beispiel: NotSupportedException/Obsolete als Skip-Kandidaten
        //if (method.Attributes.Any(a => a.Contains("Obsolete", StringComparison.OrdinalIgnoreCase)))
        //{
        //    return UnitTestSkeletonGenerator.TestStrategy.Skip;
        //}

        if (method.IsAsyncVoid)
        {
            return UnitTestSkeletonGenerator.TestStrategy.RefactorFirst;
        }

        if (isDirectlyTestable)
        {
            return UnitTestSkeletonGenerator.TestStrategy.Direct;
        }

        var onlyPrivateAccessBlocker =
            method.Accessibility == "Private"
            && blockers.Count == 1
            && blockers[0].StartsWith("Method is 'Private'", StringComparison.Ordinal);

        if (onlyPrivateAccessBlocker)
        {
            return UnitTestSkeletonGenerator.TestStrategy.Reflection;
        }

        return UnitTestSkeletonGenerator.TestStrategy.RefactorFirst;
    }

    /// <summary>
    ///     Liefert einen kurzen, LLM-freundlichen Skip-Grund für RefactorFirst.
    ///     Der vollständige Text steht weiterhin in <c>&lt;SuggestedRefactoringPattern&gt;</c>.
    /// </summary>
    private static string BuildRefactorFirstReason(MethodFact method)
    {
        if (method.IsAsyncVoid)
        {
            return "requires refactoring: async void method must become an awaitable async Task";
        }

        return "requires production-code refactoring before a test can be written";
    }

    /// <summary>
    ///     Läuft die komplette Vererbungskette hoch und prüft, ob ein bekannter
    ///     WPF-Basistyp enthalten ist. Solche Klassen dürfen nur auf einem
    ///     STA-Thread konstruiert/benutzt werden.
    /// </summary>
    private static bool DetectStaRequirement(INamedTypeSymbol? type)
    {
        if (type is null) return false;

        for (var current = type; current is not null; current = current.BaseType)
        {
            var fullName = current
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty);

            // Exact match against the known WPF base types
            if (WpfBaseTypes.Contains(fullName)) return true;

            // Fallback: unqualified name match. Happens when the compilation
            // cannot resolve WPF references (in-memory parse without references).
            var simpleName = current.Name;
            if (simpleName is "Window" or "UserControl" or "Page" or "Application"
                or "DependencyObject" or "DispatcherObject" or "FrameworkElement"
                or "UIElement" or "ContentControl" or "ItemsControl")
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- method fact

    private static MethodFact BuildMethodFact(IMethodSymbol method)
    {
        var parameters = method.Parameters.Select(p => new ParameterFact
        {
            Name = p.Name,
            Type = p.Type.ToDisplayString(FqFormat),
            IsOptional = p.IsOptional,
            HasDefaultValue = p.HasExplicitDefaultValue,
            DefaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null,
            RefKind = "None",
            IsParams = false,
            IsNullable = p.Type.NullableAnnotation == NullableAnnotation.Annotated,
            IsCancellationToken = p.Type.ToDisplayString(FqFormat) == "System.Threading.CancellationToken"
        }).ToList();

        var returnTypeName = method.ReturnType.ToDisplayString(FqFormat);
        var isTaskLike = returnTypeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) ||
                         returnTypeName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal);

        return new MethodFact
        {
            Name = method.Name,
            Signature = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ReturnType = returnTypeName,
            Accessibility = method.DeclaredAccessibility.ToString(),
            IsStatic = method.IsStatic,
            IsAsync = method.IsAsync,
            ReturnsVoid = method.ReturnsVoid,
            IsAsyncVoid = method.IsAsync && method.ReturnsVoid,
            IsVirtual = method.IsVirtual,
            IsOverride = false,
            IsAbstract = false,
            IsIterator = false,
            IsExtension = false,
            ReturnsTask = isTaskLike,
            HasCancellationToken = parameters.Any(p => p.IsCancellationToken),
            GenericParameters = new List<string>(),
            Attributes = new List<string>(),
            ThrownExceptions = new List<string>(),
            ContainingType = BuildContainingTypeFact(method.ContainingType),
            Parameters = parameters
        };
    }

    // ---------------------------------------------------------------- containing type

    private static TypeFact BuildContainingTypeFact(INamedTypeSymbol containing)
    {
        return new TypeFact
        {
            FullName = containing.ToDisplayString(FqFormat),
            Namespace = containing.ContainingNamespace?.ToDisplayString() ?? "",
            Kind = KindOf(containing),
            Accessibility = containing.DeclaredAccessibility.ToString(),
            IsStatic = containing.IsStatic,
            IsSealed = containing.IsSealed,
            IsAbstract = containing.IsAbstract,
            IsInterface = containing.TypeKind == TypeKind.Interface,
            IsRecord = containing.IsRecord,
            IsValueType = containing.IsValueType,
            IsDelegate = containing.TypeKind == TypeKind.Delegate,
            IsEnum = containing.TypeKind == TypeKind.Enum,
            Mockable = ClassMockability(containing),
            UsedStatically = false,
            DependencyKind = ClassifyDependencyKind(containing),
            BaseType = containing.BaseType?.ToDisplayString(FqFormat) ?? "",
            AllBaseTypes = new List<string>(),
            Interfaces = new List<string>(),
            Constructors = new List<string>(),
            VirtualMemberCount = 0,
            MemberCount = 0
        };
    }

    // ---------------------------------------------------------------- dependencies

    private (List<TypeFact> Types, HashSet<string> InstanceFieldTypes) CollectReferencedTypes(
        IMethodSymbol root, Compilation compilation)
    {
        var seen = new Dictionary<string, TypeFact>(StringComparer.Ordinal);
        var instanceFieldTypes = new HashSet<string>(StringComparer.Ordinal);

        void Add(ITypeSymbol? symbol, bool staticUse, UsageKind usage)
        {
            if (symbol is null) return;
            if (symbol is ITypeParameterSymbol) return;
            if (symbol.SpecialType != SpecialType.None) return;
            if (symbol is not INamedTypeSymbol named) return;

            if (named.IsGenericType)
            {
                foreach (var arg in named.TypeArguments) Add(arg, staticUse, usage);
            }

            var full = named.OriginalDefinition.ToDisplayString(FqFormat);
            if (string.IsNullOrWhiteSpace(full)) return;

            if (full is "System.Threading.Tasks.Task<>" or "System.Threading.Tasks.ValueTask<>" or "System.Threading.Tasks.Task")
                return;
            if (full.StartsWith("System.Nullable", StringComparison.Ordinal)) return;

            var isFramework = named.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true;

            if (isFramework && !StaticApiAbstractions.ContainsKey(full))
                return;

            if (seen.TryGetValue(full, out var existing))
            {
                seen[full] = new TypeFact
                {
                    FullName = existing.FullName,
                    Namespace = existing.Namespace,
                    Kind = existing.Kind,
                    Accessibility = existing.Accessibility,
                    IsStatic = existing.IsStatic,
                    IsSealed = existing.IsSealed,
                    IsAbstract = existing.IsAbstract,
                    IsInterface = existing.IsInterface,
                    IsRecord = existing.IsRecord,
                    IsValueType = existing.IsValueType,
                    IsDelegate = existing.IsDelegate,
                    IsEnum = existing.IsEnum,
                    Mockable = existing.Mockable,
                    UsedStatically = existing.UsedStatically || staticUse,
                    DependencyKind = existing.DependencyKind,
                    Usages = existing.Usages | usage,
                    BaseType = existing.BaseType,
                    AllBaseTypes = new List<string>(),
                    RecommendedAbstraction = existing.RecommendedAbstraction,
                    RecommendedAbstractionPackage = existing.RecommendedAbstractionPackage,
                    RecommendationReason = existing.RecommendationReason,
                    Interfaces = new List<string>(),
                    Constructors = new List<string>(),
                    VirtualMemberCount = 0,
                    MemberCount = 0
                };
                return;
            }

            var abstraction = ResolveAbstraction(named);
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
                IsRecord = named.IsRecord,
                IsValueType = named.IsValueType,
                IsDelegate = named.TypeKind == TypeKind.Delegate,
                IsEnum = named.TypeKind == TypeKind.Enum,
                Mockable = ClassMockability(named),
                UsedStatically = staticUse,
                DependencyKind = ClassifyDependencyKind(named),
                Usages = usage,
                BaseType = named.BaseType?.ToDisplayString(FqFormat) ?? "",
                AllBaseTypes = new List<string>(),
                RecommendedAbstraction = abstraction.Abstraction,
                RecommendedAbstractionPackage = abstraction.Package,
                RecommendationReason = abstraction.Reason,
                Constructors = new List<string>(),
                Interfaces = new List<string>(),
                VirtualMemberCount = 0,
                MemberCount = 0
            };
        }

        foreach (var syntaxRef in root.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax decl) continue;

            var model = compilation.GetSemanticModel(decl.SyntaxTree);
            if (model.GetOperation(decl) is not IMethodBodyOperation body) continue;

            foreach (var op in body.Descendants())
            {
                switch (op)
                {
                    case IInvocationOperation inv:
                        Add(inv.TargetMethod.ContainingType, inv.TargetMethod.IsStatic, UsageKind.Call);
                        Add(inv.TargetMethod.ReturnType as INamedTypeSymbol, false, UsageKind.Call);
                        if (inv.Instance?.Type is INamedTypeSymbol instType)
                            Add(instType, false, UsageKind.Call);
                        break;

                    case IObjectCreationOperation oc:
                        Add(oc.Type, false, UsageKind.Create);
                        break;

                    case IFieldReferenceOperation fr:
                        Add(fr.Field.ContainingType, fr.Field.IsStatic, UsageKind.Read);
                        Add(fr.Field.Type as INamedTypeSymbol, false, UsageKind.Read);
                        if (!fr.Field.IsStatic &&
                            SymbolEqualityComparer.Default.Equals(fr.Field.ContainingType, root.ContainingType))
                        {
                            var fieldTypeName = (fr.Field.Type as INamedTypeSymbol)?
                                .OriginalDefinition.ToDisplayString(FqFormat);
                            if (!string.IsNullOrEmpty(fieldTypeName))
                                instanceFieldTypes.Add(fieldTypeName!);
                        }
                        break;

                    case IPropertyReferenceOperation pr:
                        Add(pr.Property.ContainingType, pr.Property.IsStatic, UsageKind.Read);
                        Add(pr.Property.Type as INamedTypeSymbol, false, UsageKind.Read);
                        break;

                    case IParameterReferenceOperation par:
                        Add(par.Parameter.Type as INamedTypeSymbol, false, UsageKind.Read);
                        break;

                    case ILocalReferenceOperation lr:
                        Add(lr.Local.Type as INamedTypeSymbol, false, UsageKind.Read);
                        break;

                    case IVariableDeclaratorOperation vd:
                        Add(vd.Symbol.Type as INamedTypeSymbol, false, UsageKind.Read);
                        break;

                    case IAwaitOperation aw:
                        Add(aw.Operation.Type as INamedTypeSymbol, false, UsageKind.Await);
                        break;
                }
            }
        }

        var ordered = seen.Values
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        return (ordered, instanceFieldTypes);
    }

    // ---------------------------------------------------------------- evaluation

    private static (string Text, bool IsDirectlyTestable, List<string> Blockers) EvaluateTestability(
        MethodFact method, List<TypeFact> types, HashSet<string> instanceFieldTypes)
    {
        var blockers = new List<string>();

        if (method.Accessibility is "Private" or "Protected" or "ProtectedAndInternal")
            blockers.Add($"Method is '{method.Accessibility}' and cannot be called directly.");

        if (method.IsAsyncVoid)
            blockers.Add("Method is 'async void' and cannot be awaited.");

        var containingTypeName = method.ContainingType?.FullName;
        foreach (var t in types
                     .Where(t => t.UsedStatically)
                     .Where(t => !string.Equals(t.FullName, containingTypeName, StringComparison.Ordinal))
                     .Where(IsRealStaticBlocker))
        {
            blockers.Add($"Static dependency on '{t.FullName}'.");
        }

        foreach (var fullName in instanceFieldTypes)
        {
            if (string.Equals(fullName, containingTypeName, StringComparison.Ordinal)) continue;

            var t = types.FirstOrDefault(x => x.FullName == fullName);
            if (t == null) continue;
            if (t.IsInterface || t.IsAbstract || t.IsStatic) continue;
            if (t.IsValueType || t.IsEnum || t.IsDelegate) continue;
            if (t.Namespace.StartsWith("System", StringComparison.Ordinal)) continue;

            blockers.Add($"Concrete dependency '{fullName}' is created internally.");
        }

        var isTestable = blockers.Count == 0;
        var text = isTestable ? "Directly testable." : "NOT directly testable: " + string.Join(" | ", blockers);
        return (text, isTestable, blockers);
    }

    private static bool IsRealStaticBlocker(TypeFact type) =>
        StaticApiAbstractions.ContainsKey(type.FullName);

    // ---------------------------------------------------------------- recommendations

    private static (List<string> SourceRefactoring, List<string> TestStrategy) BuildRecommendations(
        MethodFact method, List<TypeFact> types, HashSet<string> instanceFieldTypes)
    {
        var sourceRefactoring = new List<string>();
        var testStrategy = new List<string>();

        if (method.Accessibility is "Private" or "Protected")
            sourceRefactoring.Add("Make method 'internal' and add InternalsVisibleTo.");

        if (method.IsAsyncVoid)
            sourceRefactoring.Add("Change 'async void' to 'async Task'.");

        foreach (var t in types.Where(t => t.UsedStatically && IsRealStaticBlocker(t)))
        {
            if (!string.IsNullOrEmpty(t.RecommendedAbstraction))
            {
                sourceRefactoring.Add(t.RecommendedAbstractionPackage is null
                    ? $"Inject '{t.RecommendedAbstraction}' instead of static '{t.FullName}'."
                    : $"Inject '{t.RecommendedAbstraction}' (package '{t.RecommendedAbstractionPackage}') instead of static '{t.FullName}'.");
            }
        }

        var nonInjectable = types
            .Where(t => instanceFieldTypes.Contains(t.FullName))
            .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsStatic)
            .Where(t => !t.Namespace.StartsWith("System", StringComparison.Ordinal))
            .ToList();

        if (nonInjectable.Count > 0)
            sourceRefactoring.Add("Use constructor injection for: " +
                                  string.Join(", ", nonInjectable.Select(t => t.FullName)));

        if (method.HasCancellationToken)
            testStrategy.Add("Cover cancellation with CancellationToken.None and a cancelled token.");

        if (method.ReturnsTask)
            testStrategy.Add("Test method must be 'async Task' and await the result.");

        if (method.IsStatic && method.Accessibility == "Public")
            testStrategy.Add("Method is a public static entry point; call it directly and assert the result.");

        return (sourceRefactoring.Distinct().ToList(), testStrategy.Distinct().ToList());
    }

    // ---------------------------------------------------------------- helpers

    private static (string Abstraction, string? Package, string Reason) ResolveAbstraction(INamedTypeSymbol type)
    {
        var full = type.OriginalDefinition.ToDisplayString(FqFormat);
        if (StaticApiAbstractions.TryGetValue(full, out var mapped))
        {
            return (mapped.Abstraction, mapped.Package, $"static API '{full}'");
        }
        if (type.IsStatic) return ("", null, "static type - cannot be mocked.");
        return ("", null, "");
    }

    private static string KindOf(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Class => type.IsRecord ? "Record" : "Class",
        TypeKind.Interface => "Interface",
        TypeKind.Struct => type.IsRecord ? "RecordStruct" : "Struct",
        TypeKind.Enum => "Enum",
        TypeKind.Delegate => "Delegate",
        _ => type.TypeKind.ToString()
    };

    private static DependencyKind ClassifyDependencyKind(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface) return DependencyKind.Interface;
        if (type.TypeKind == TypeKind.Delegate) return DependencyKind.Delegate;
        if (type.TypeKind == TypeKind.Enum) return DependencyKind.Enum;
        if (type.IsStatic) return DependencyKind.StaticClass;
        if (type.IsAbstract) return DependencyKind.AbstractClass;
        if (type.IsSealed) return DependencyKind.SealedClass;
        if (type.IsValueType) return DependencyKind.Struct;
        if (type.SpecialType != SpecialType.None) return DependencyKind.Primitive;
        if (type.TypeKind == TypeKind.Class) return DependencyKind.ConcreteClass;
        return DependencyKind.Unknown;
    }

    private static string ClassMockability(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface) return "Yes (interface)";
        if (type.IsStatic) return "No (static)";
        if (type.IsSealed) return "No (sealed)";
        if (type.IsAbstract) return "Yes (abstract)";
        var virtualCount = type.GetMembers().Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride);
        return virtualCount == 0 ? "No (no virtual members)" : "Limited (virtual members exist)";
    }

    private static string ResolveDocumentName(string? requestedName, SyntaxTree tree)
    {
        if (!string.IsNullOrWhiteSpace(requestedName)) return requestedName!;
        var fromPath = Path.GetFileName(tree.FilePath);
        return string.IsNullOrEmpty(fromPath) ? "(unnamed)" : fromPath;
    }

    private static bool MatchesDocumentPath(string? treePath, string documentName)
    {
        if (string.IsNullOrEmpty(treePath)) return false;
        var file = Path.GetFileName(treePath);
        return string.Equals(file, documentName, StringComparison.OrdinalIgnoreCase) ||
               treePath.Contains(documentName, StringComparison.OrdinalIgnoreCase);
    }

    private static (SyntaxTree Tree, MethodDeclarationSyntax Method)? FindMethodAcrossTrees(
        Compilation compilation, string methodName, string? documentName, CancellationToken ct)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(documentName) && !MatchesDocumentPath(tree.FilePath, documentName!))
                continue;

            var root = tree.GetRoot(ct);
            var candidates = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text == methodName).ToList();

            if (candidates.Count == 0) continue;

            var preferred = candidates.FirstOrDefault(m =>
                                m.Modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword)) && m.TypeParameterList == null)
                            ?? candidates[0];

            return (tree, preferred);
        }
        return null;
    }

#if !NETSTANDARD2_0
/// <summary>
///     MSBuild properties required in the analyzer host so that WPF references
///     (System.Windows.*) resolve correctly. Without CheckForSystemRuntimeDependency
///     the WPF assemblies are not loaded into the compilation.
/// </summary>
private static IReadOnlyDictionary<string, string> MsBuildProperties { get; } =
    new Dictionary<string, string>
    {
        ["DesignTimeBuild"] = "true",
        ["CheckForSystemRuntimeDependency"] = "true"
    };

/// <summary>
///     Creates an <see cref="MSBuildWorkspace" /> configured for design-time builds
///     with system runtime dependencies. Returns a fresh dictionary on each call
///     because MSBuildWorkspace.Create mutates the dictionary it receives.
/// </summary>
private static MSBuildWorkspace CreateMsBuildWorkspace()
{
    var properties = new Dictionary<string, string>(MsBuildProperties);
    return MSBuildWorkspace.Create(properties);
}
#endif

}