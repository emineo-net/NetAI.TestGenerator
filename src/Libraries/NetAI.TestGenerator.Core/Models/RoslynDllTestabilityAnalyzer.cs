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
using NetAI.TestGenerator.Core.Models;
using NetAI.TestGenerator.Core.Models.Enums;

#if !NETSTANDARD2_0
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
#endif

namespace NetAI.TestGenerator.Core.Models
{
    /// <summary>
    /// Analyzes testability using Roslyn. Supports two load strategies:
    /// (a) a pre-built <see cref="Compilation"/> (fast, works on all TFMs),
    /// (b) MSBuild workspace loading of a solution or project (accurate, net10.0+).
    /// </summary>
    public sealed class RoslynDllTestabilityAnalyzer
    {
        private static readonly SymbolDisplayFormat FqFormat =
            SymbolDisplayFormat.FullyQualifiedFormat
                .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

        private static readonly IReadOnlyDictionary<string, string> StaticApiAbstractions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["System.IO.File"] = "System.IO.Abstractions.IFileSystem",
                ["System.IO.Directory"] = "System.IO.Abstractions.IFileSystem",
                ["System.IO.Path"] = "System.IO.Abstractions.IPath",
                ["System.IO.FileStream"] = "System.IO.Abstractions.IFileSystem",
                ["System.DateTime"] = "System.TimeProvider (or custom ITimeProvider)",
                ["System.DateTimeOffset"] = "System.TimeProvider (or custom ITimeProvider)",
                ["System.Guid"] = "custom IGuidProvider",
                ["System.Random"] = "custom IRandom",
                ["System.Environment"] = "custom IEnvironment",
                ["System.Console"] = "custom IConsole",
                ["System.Net.Http.HttpClient"] = "System.Net.Http.IHttpClientFactory",
                ["System.Threading.Thread"] = "avoid - use Task / TimeProvider",
                ["System.Diagnostics.Process"] = "abstraction over IProcessRunner",
                ["System.IO.Compression.ZipFile"] = "abstraction over IZipService",
            };

        private readonly AnalyzerOptions _options;

        public RoslynDllTestabilityAnalyzer(AnalyzerOptions? options = null)
            => _options = options ?? new AnalyzerOptions();

        // ---------------------------------------------------------------- public API

        public Task<TestabilityReport> AnalyzeFromSourceFilesAsync(
            IEnumerable<string> sourceFilePaths,
            IEnumerable<string> referenceDllPaths,
            string methodName,
            string? documentName = null,
            CancellationToken ct = default)
        {
            var compilation = BuildCompilation(sourceFilePaths, referenceDllPaths);
            return AnalyzeFromCompilationAsync(compilation, methodName, documentName, ct);
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
                throw new ArgumentException("Directory must not be empty.", nameof(directory));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(directory, searchPattern, option)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .ToList();

            return AnalyzeFromSourceFilesAsync(files, referenceDllPaths, methodName, documentName, ct);
        }

        /// <summary>Analyzes a method by name (first match wins). Prefer the syntax overload.</summary>
        public Task<TestabilityReport> AnalyzeFromCompilationAsync(
            Compilation compilation,
            string methodName,
            string? documentName = null,
            CancellationToken ct = default)
        {
            if (compilation is null) throw new ArgumentNullException(nameof(compilation));

            var (tree, methodDecl) = FindMethodAcrossTrees(compilation, methodName, documentName, ct)
                                     ?? throw new InvalidOperationException(
                                         $"Method '{methodName}' was not found in any source file.");

            var model = compilation.GetSemanticModel(tree);
            var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol
                               ?? throw new InvalidOperationException("No method symbol was found.");

            var documentNameResolved = ResolveDocumentName(documentName, tree);
            var report = BuildReport(documentNameResolved, methodSymbol, methodDecl, model, compilation);
            return Task.FromResult(report);
        }

        /// <summary>
        /// Analyzes a method from a syntax node. If the node does not belong to the
        /// compilation (e.g. it was parsed separately), the equivalent declaration is
        /// located inside the compilation by file path, method name and parameter count.
        /// </summary>
        public Task<TestabilityReport> AnalyzeFromCompilationAsync(
            Compilation compilation,
            MethodDeclarationSyntax methodDeclaration,
            CancellationToken ct = default)
        {
            if (compilation is null) throw new ArgumentNullException(nameof(compilation));
            if (methodDeclaration is null) throw new ArgumentNullException(nameof(methodDeclaration));

            var (tree, localDecl) = FindEquivalentDeclaration(compilation, methodDeclaration, ct)
                ?? throw new InvalidOperationException(
                    $"Method '{methodDeclaration.Identifier.Text}' was not found in the compilation.");

            var model = compilation.GetSemanticModel(tree);
            var methodSymbol = model.GetDeclaredSymbol(localDecl, ct) as IMethodSymbol
                               ?? throw new InvalidOperationException("No method symbol was found.");

            var documentNameResolved = ResolveDocumentName(null, tree);
            var report = BuildReport(documentNameResolved, methodSymbol, localDecl, model, compilation);
            return Task.FromResult(report);
        }

        // ---------------------------------------------------------------- MSBuild-based loading

#if !NETSTANDARD2_0
        /// <summary>
        /// Loads a solution through MSBuild and analyzes a named method in the requested document.
        /// Use this when you need full project context (NuGet, WPF, Directory.Build.props).
        /// </summary>
        /// <param name="solutionPath">Path to the .sln file.</param>
        /// <param name="documentName">File name or path fragment of the document.</param>
        /// <param name="methodName">Name of the method to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<TestabilityReport> AnalyzeFromSolutionAsync(
            string solutionPath,
            string documentName,
            string methodName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                throw new ArgumentException("Solution path must not be empty.", nameof(solutionPath));
            if (!File.Exists(solutionPath))
                throw new FileNotFoundException("Solution file not found.", solutionPath);

            EnsureMSBuildRegistered();

            using var workspace = MSBuildWorkspace.Create();
            workspace.SkipUnrecognizedProjects = true;
            workspace.WorkspaceFailed += (_, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[MSBuildWorkspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");
            };

            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct)
                .ConfigureAwait(false);

            var document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => MatchesDocument(d, documentName))
                ?? throw new InvalidOperationException(
                    $"Document '{documentName}' was not found in solution '{Path.GetFileName(solutionPath)}'.");

            return await AnalyzeDocumentAsync(document, methodName, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads a project through MSBuild and analyzes a named method in the requested document.
        /// Use this when you need full project context (NuGet, WPF, Directory.Build.props).
        /// </summary>
        public async Task<TestabilityReport> AnalyzeFromProjectAsync(
            string projectPath,
            string documentName,
            string methodName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Project path must not be empty.", nameof(projectPath));
            if (!File.Exists(projectPath))
                throw new FileNotFoundException("Project file not found.", projectPath);

            EnsureMSBuildRegistered();

            using var workspace = MSBuildWorkspace.Create();
            workspace.SkipUnrecognizedProjects = true;
            workspace.WorkspaceFailed += (_, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[MSBuildWorkspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");
            };

            var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct)
                .ConfigureAwait(false);

            var document = project.Documents
                .FirstOrDefault(d => MatchesDocument(d, documentName))
                ?? throw new InvalidOperationException(
                    $"Document '{documentName}' was not found in project '{Path.GetFileName(projectPath)}'.");

            return await AnalyzeDocumentAsync(document, methodName, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Analyzes a method in an already loaded Roslyn <see cref="Document"/>.
        /// The MSBuild workspace owns the document's lifetime, so do not dispose it here.
        /// </summary>
        public async Task<TestabilityReport> AnalyzeDocumentAsync(
            Document document,
            string methodName,
            CancellationToken ct = default)
        {
            if (document is null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(methodName))
                throw new ArgumentException("Method name must not be empty.", nameof(methodName));

            var compilation = await document.Project.GetCompilationAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Compilation could not be created for the document's project.");
            var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Syntax tree is missing for the document.");

            var model = compilation.GetSemanticModel(tree);

            var methodDecl = tree.GetRoot(ct)
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName)
                ?? throw new InvalidOperationException(
                    $"Method '{methodName}' was not found in document '{document.Name}'.");

            var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol
                               ?? throw new InvalidOperationException("No method symbol was found.");

            return BuildReport(document.Name, methodSymbol, methodDecl, model, compilation);
        }

        /// <summary>
        /// Loads a solution or project once and returns the workspace (caller disposes)
        /// plus the target document's compilation. Use this when you need to analyze
        /// multiple methods in the same file without reloading MSBuild each time.
        /// </summary>
        /// <param name="solutionPath">Optional .sln path. Takes precedence over <paramref name="projectPath"/>.</param>
        /// <param name="projectPath">Optional .csproj path.</param>
        /// <param name="documentName">File name or path fragment of the document to locate.</param>
        /// <param name="logInfo">Optional callback for workspace diagnostics.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<(IDisposable Workspace, Compilation? Compilation)> LoadCompilationFromMsbuildAsync(
            string? solutionPath,
            string? projectPath,
            string documentName,
            Action<string>? logInfo = null,
            CancellationToken ct = default)
        {
            EnsureMSBuildRegistered();

            var workspace = MSBuildWorkspace.Create();
            workspace.SkipUnrecognizedProjects = true;
            workspace.WorkspaceFailed += (_, e) =>
            {
                logInfo?.Invoke($"[MSBuildWorkspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");
            };

            Document? document = null;

            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct)
                    .ConfigureAwait(false);
                document = solution.Projects
                    .SelectMany(p => p.Documents)
                    .FirstOrDefault(d => MatchesDocument(d, documentName));
            }
            else if (!string.IsNullOrWhiteSpace(projectPath))
            {
                var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct)
                    .ConfigureAwait(false);
                document = project.Documents
                    .FirstOrDefault(d => MatchesDocument(d, documentName));
            }

            if (document == null)
            {
                workspace.Dispose();
                return (new NoopDisposable(), null);
            }

            var compilation = await document.Project.GetCompilationAsync(ct).ConfigureAwait(false);
            return (workspace, compilation);
        }

        /// <summary>
        /// Registers MSBuild once per process. <c>MSBuildLocator.RegisterDefaults()</c> throws
        /// if called twice, so this helper is idempotent.
        /// </summary>
        private static void EnsureMSBuildRegistered()
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }

        /// <summary>Matches a document by exact name, file name, or path fragment.</summary>
        private static bool MatchesDocument(Document document, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (string.Equals(document.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;

            var path = document.FilePath;
            if (string.IsNullOrEmpty(path)) return false;

            return path.EndsWith(name, StringComparison.OrdinalIgnoreCase)
                || path.Contains(name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Disposable placeholder when no workspace needs to be released.</summary>
        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
#endif

        // ---------------------------------------------------------------- equivalent lookup

        /// <summary>
        /// Locates the compilation-owned declaration that corresponds to an externally
        /// parsed <see cref="MethodDeclarationSyntax"/>. Match key: file path (or file
        /// name), method identifier, and parameter count.
        /// </summary>
        private static (SyntaxTree Tree, MethodDeclarationSyntax Method)? FindEquivalentDeclaration(
            Compilation compilation,
            MethodDeclarationSyntax external,
            CancellationToken ct)
        {
            var methodName = external.Identifier.Text;
            var paramCount = external.ParameterList.Parameters.Count;

            var externalPath = external.SyntaxTree?.FilePath;
            var externalFileName = string.IsNullOrEmpty(externalPath)
                ? null
                : Path.GetFileName(externalPath);

            // 1) Match by exact file path (or, if paths differ, by file name).
            if (!string.IsNullOrEmpty(externalPath) || !string.IsNullOrEmpty(externalFileName))
            {
                foreach (var tree in compilation.SyntaxTrees)
                {
                    ct.ThrowIfCancellationRequested();

                    var treePath = tree.FilePath;
                    if (string.IsNullOrEmpty(treePath)) continue;

                    var sameFullPath = !string.IsNullOrEmpty(externalPath)
                        && string.Equals(treePath, externalPath, StringComparison.OrdinalIgnoreCase);

                    var sameFileName = !string.IsNullOrEmpty(externalFileName)
                        && string.Equals(Path.GetFileName(treePath), externalFileName, StringComparison.OrdinalIgnoreCase);

                    if (!sameFullPath && !sameFileName) continue;

                    var match = FindMethodInTree(tree, methodName, paramCount, ct);
                    if (match != null) return (tree, match);
                }
            }

            // 2) Fallback: search all trees by name + arity.
            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                var match = FindMethodInTree(tree, methodName, paramCount, ct);
                if (match != null) return (tree, match);
            }

            return null;
        }

        private static MethodDeclarationSyntax? FindMethodInTree(
            SyntaxTree tree,
            string methodName,
            int paramCount,
            CancellationToken ct)
        {
            var root = tree.GetRoot(ct);
            return root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m =>
                    m.Identifier.Text == methodName &&
                    m.ParameterList.Parameters.Count == paramCount);
        }

        // ---------------------------------------------------------------- compilation

        public static CSharpCompilation BuildCompilation(
            IEnumerable<string> sourceFilePaths,
            IEnumerable<string> referenceDllPaths,
            string assemblyName = "TestabilityAnalysis",
            CSharpParseOptions? parseOptions = null,
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
                    trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: path));
                }
                catch { /* ignore unreadable files */ }
            }

            var references = new List<MetadataReference>();
            if (referenceDllPaths != null)
            {
                foreach (var dll in referenceDllPaths)
                {
                    if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) continue;
                    try { references.Add(MetadataReference.CreateFromFile(dll)); }
                    catch { /* ignore invalid references */ }
                }
            }

            return CSharpCompilation.Create(
                assemblyName,
                syntaxTrees: trees,
                references: references,
                options: compilationOptions ?? new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug));
        }

        // ---------------------------------------------------------------- report assembly

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
            var typeFacts = CollectReferencedTypes(method, compilation);
            var verdict = EvaluateTestability(methodFact, typeFacts);
            var recs = BuildRecommendations(methodFact, typeFacts);
            var callGraph = BuildCallGraph(method, compilation);

            return new TestabilityReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                DocumentName = documentName,
                Method = methodFact,
                ReferencedTypes = typeFacts,
                Verdict = verdict.Text,
                IsDirectlyTestable = verdict.IsDirectlyTestable,
                Blockers = verdict.Blockers,
                Recommendations = recs,
                CompilationErrors = errors,
                AnalyzedCallGraph = callGraph,
            };
        }

        // ---------------------------------------------------------------- method fact

        private static MethodFact BuildMethodFact(IMethodSymbol method)
        {
            var containing = method.ContainingType;
            var parameters = method.Parameters.Select(p => new ParameterFact
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(FqFormat),
                IsOptional = p.IsOptional,
                HasDefaultValue = p.HasExplicitDefaultValue,
                DefaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null,
                RefKind = p.RefKind.ToString(),
                IsParams = p.IsParams,
                IsNullable = p.Type.NullableAnnotation == NullableAnnotation.Annotated,
                IsCancellationToken = p.Type.ToDisplayString(FqFormat) == "System.Threading.CancellationToken",
            }).ToList();

            var attributes = method.GetAttributes()
                .Select(a => a.AttributeClass?.ToDisplayString(FqFormat) ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            var thrown = new List<string>();
            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax decl) continue;
                foreach (var throwStmt in decl.DescendantNodes().OfType<ThrowStatementSyntax>())
                {
                    var expr = throwStmt.Expression?.ToString();
                    if (!string.IsNullOrEmpty(expr)) thrown.Add(expr);
                }
            }

            var genericParameters = method.TypeParameters
                .Select(tp => tp.Name + (tp.HasReferenceTypeConstraint ? " : class" :
                                        tp.HasValueTypeConstraint ? " : struct" : ""))
                .ToList();

            var returnTypeName = method.ReturnType.ToDisplayString(FqFormat);
            var isTaskLike = returnTypeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal)
                             || returnTypeName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal);

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
                IsOverride = method.IsOverride,
                IsAbstract = method.IsAbstract,
                IsIterator = method.IsIterator,
                IsExtension = method.IsExtensionMethod,
                ReturnsTask = isTaskLike,
                HasCancellationToken = parameters.Any(p => p.IsCancellationToken),
                GenericParameters = genericParameters,
                Attributes = attributes,
                ThrownExceptions = thrown.Distinct().ToList(),
                ContainingType = BuildContainingTypeFact(containing),
                Parameters = parameters,
            };
        }

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
                Interfaces = containing.AllInterfaces.Select(i => i.ToDisplayString(FqFormat)).ToList(),
                Constructors = containing.InstanceConstructors
                    .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                    .Select(c => c.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                    .ToList(),
                VirtualMemberCount = containing.GetMembers()
                    .Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride),
                MemberCount = containing.GetMembers().Length,
            };
        }

        // ---------------------------------------------------------------- call graph + dependencies

        private IReadOnlyList<string> BuildCallGraph(IMethodSymbol root, Compilation compilation)
        {
            if (!_options.IncludeRecursiveCallGraph)
                return new[] { root.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) };

            var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var ordered = new List<string>();
            var queue = new Queue<(IMethodSymbol method, int depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                var (method, depth) = queue.Dequeue();
                if (!visited.Add(method)) continue;

                ordered.Add(method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                if (depth >= _options.MaxCallGraphDepth) continue;

                foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                {
                    if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax decl) continue;
                    var model = compilation.GetSemanticModel(decl.SyntaxTree);
                    if (model.GetOperation(decl) is not IMethodBodyOperation body) continue;

                    foreach (var inv in body.Descendants().OfType<IInvocationOperation>())
                    {
                        var target = inv.TargetMethod;
                        if (target == null) continue;
                        if (target.MethodKind == MethodKind.DelegateInvoke) continue;
                        if (!SymbolEqualityComparer.Default.Equals(target.ContainingType, method.ContainingType))
                            continue;
                        if (!visited.Contains(target))
                            queue.Enqueue((target, depth + 1));
                    }
                }
            }

            return ordered;
        }

        private List<TypeFact> CollectReferencedTypes(IMethodSymbol root, Compilation compilation)
        {
            var seen = new Dictionary<string, TypeFact>(StringComparer.Ordinal);

            void Add(ITypeSymbol? symbol, bool staticUse, UsageKind usage)
            {
                if (symbol is null) return;
                if (symbol is ITypeParameterSymbol) return;
                if (symbol.SpecialType != SpecialType.None) return;
                if (symbol is not INamedTypeSymbol named) return;

                if (named.IsGenericType)
                {
                    foreach (var arg in named.TypeArguments)
                        Add(arg, staticUse, usage);
                }

                var full = named.OriginalDefinition.ToDisplayString(FqFormat);
                if (string.IsNullOrWhiteSpace(full)) return;
                if (full is "System.Threading.Tasks.Task<>"
                    or "System.Threading.Tasks.ValueTask<>"
                    or "System.Threading.Tasks.Task") return;

                if (full.StartsWith("System.Nullable", StringComparison.Ordinal)) return;

                var isFramework = named.ContainingNamespace?.ToDisplayString()
                    .StartsWith("System", StringComparison.Ordinal) == true;

                if (isFramework && !_options.IncludeFrameworkTypes && !staticUse) return;

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
                        RecommendedAbstraction = existing.RecommendedAbstraction,
                        RecommendationReason = existing.RecommendationReason,
                        Interfaces = existing.Interfaces,
                        Constructors = existing.Constructors,
                        VirtualMemberCount = existing.VirtualMemberCount,
                        MemberCount = existing.MemberCount,
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
                    RecommendedAbstraction = abstraction.Abstraction,
                    RecommendationReason = abstraction.Reason,
                    Constructors = named.InstanceConstructors
                        .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                        .Select(c => c.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                        .ToList(),
                    Interfaces = named.AllInterfaces.Select(i => i.ToDisplayString(FqFormat)).ToList(),
                    VirtualMemberCount = named.GetMembers()
                        .Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride),
                    MemberCount = named.GetMembers().Length,
                };
            }

            var methodsToInspect = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { root };
            if (_options.IncludeRecursiveCallGraph)
            {
                foreach (var syntaxRef in root.DeclaringSyntaxReferences)
                {
                    if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax rootDecl) continue;
                    var model = compilation.GetSemanticModel(rootDecl.SyntaxTree);
                    if (model.GetOperation(rootDecl) is not IMethodBodyOperation body) continue;

                    foreach (var inv in body.Descendants().OfType<IInvocationOperation>())
                    {
                        var t = inv.TargetMethod;
                        if (t == null) continue;
                        if (!SymbolEqualityComparer.Default.Equals(t.ContainingType, root.ContainingType)) continue;
                        methodsToInspect.Add(t);
                    }
                }
            }

            foreach (var method in methodsToInspect)
            {
                foreach (var syntaxRef in method.DeclaringSyntaxReferences)
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
                                Add(inv.TargetMethod.ReturnType as INamedTypeSymbol, inv.TargetMethod.IsStatic, UsageKind.Call);
                                if (inv.Instance?.Type is INamedTypeSymbol instType)
                                    Add(instType, false, UsageKind.Call);
                                break;

                            case IObjectCreationOperation oc:
                                Add(oc.Type, false, UsageKind.Create);
                                break;

                            case IFieldReferenceOperation fr:
                                Add(fr.Field.ContainingType, fr.Field.IsStatic, UsageKind.Read);
                                Add(fr.Field.Type as INamedTypeSymbol, fr.Field.IsStatic, UsageKind.Read);
                                break;

                            case IPropertyReferenceOperation pr:
                                Add(pr.Property.ContainingType, pr.Property.IsStatic, UsageKind.Read);
                                Add(pr.Property.Type as INamedTypeSymbol, pr.Property.IsStatic, UsageKind.Read);
                                break;

                            case IEventReferenceOperation er:
                                Add(er.Event.ContainingType, er.Event.IsStatic, UsageKind.Read);
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

                            case IThrowOperation th:
                                if (th.Exception?.Type is INamedTypeSymbol exType)
                                    Add(exType, false, UsageKind.Throw);
                                break;

                            case ITypeOfOperation to:
                                Add(to.TypeOperand, false, UsageKind.TypeOf);
                                break;
                        }
                    }
                }
            }

            return seen.Values
                .OrderBy(t => t.Namespace.StartsWith("System", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
        }

        // ---------------------------------------------------------------- evaluation

        private static (string Text, bool IsDirectlyTestable, List<string> Blockers) EvaluateTestability(
            MethodFact method, List<TypeFact> types)
        {
            var blockers = new List<string>();

            if (method.Accessibility is "Private" or "Protected" or "ProtectedAndInternal")
                blockers.Add($"Method is '{method.Accessibility}' and cannot be called directly.");

            if (method.IsAsyncVoid)
                blockers.Add("Method is 'async void' and cannot be awaited.");

            if (method.IsStatic)
                blockers.Add("Method is 'static' and difficult to isolate.");

            foreach (var t in types.Where(t => t.UsedStatically))
                blockers.Add($"Static dependency on '{t.FullName}' - cannot be mocked.");

            if (method.ContainingType is { IsSealed: true })
                blockers.Add("Containing type is sealed - cannot be subclassed for test doubles.");

            if (method.ContainingType is { IsStatic: true })
                blockers.Add("Containing type is static - test must rely on public entry points only.");

            if (method.ContainingType is { Constructors.Count: 0 })
                blockers.Add("Containing type has no public constructor - instantiation in test may require reflection or a factory.");

            var isTestable = blockers.Count == 0;
            var text = isTestable
                ? "Directly testable."
                : "NOT directly testable: " + string.Join(" | ", blockers);

            return (text, isTestable, blockers);
        }

        private static List<string> BuildRecommendations(MethodFact method, List<TypeFact> types)
        {
            var recs = new List<string>();

            if (method.Accessibility is "Private" or "Protected")
                recs.Add("Set accessibility to 'internal' and add InternalsVisibleTo, or move the logic into a separate class.");

            if (method.IsAsyncVoid)
                recs.Add("Change the event handler to 'async Task'; keep the XAML handler as a thin wrapper.");

            foreach (var t in types.Where(t => t.UsedStatically))
            {
                if (!string.IsNullOrEmpty(t.RecommendedAbstraction))
                    recs.Add($"'{t.FullName}' -> use '{t.RecommendedAbstraction}' and mock it (e.g. with Moq / NSubstitute).");
                else if (!t.IsInterface)
                    recs.Add($"'{t.FullName}' is used statically. Put it behind an interface so it can be mocked.");
            }

            var concrete = types
                .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsStatic && !t.IsSealed
                            && !t.Namespace.StartsWith("System", StringComparison.Ordinal))
                .ToList();

            if (concrete.Count > 0)
                recs.Add("Concrete types are created internally; prefer constructor injection: " +
                         string.Join(", ", concrete.Select(t => t.FullName)));

            if (method.HasCancellationToken)
                recs.Add("Method accepts a CancellationToken - pass CancellationToken.None or a cancelled token in the test.");

            if (method.ReturnsTask)
                recs.Add("Method returns Task/ValueTask - test must be async and await the result.");

            return recs.Distinct().ToList();
        }

        // ---------------------------------------------------------------- helpers

        private static (string Abstraction, string Reason) ResolveAbstraction(INamedTypeSymbol type)
        {
            var full = type.OriginalDefinition.ToDisplayString(FqFormat);
            if (StaticApiAbstractions.TryGetValue(full, out var mapped))
                return (mapped, $"'{full}' is a well-known static API without an abstraction.");

            if (type.IsStatic)
                return ("", "static type - cannot be mocked.");

            return ("", "");
        }

        private static string KindOf(INamedTypeSymbol type) => type.TypeKind switch
        {
            TypeKind.Class => type.IsRecord ? "Record" : "Class",
            TypeKind.Interface => "Interface",
            TypeKind.Struct => type.IsRecord ? "RecordStruct" : "Struct",
            TypeKind.Enum => "Enum",
            TypeKind.Delegate => "Delegate",
            _ => type.TypeKind.ToString(),
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

            var virtualCount = type.GetMembers()
                .Count(m => m.IsVirtual || m.IsAbstract || m.IsOverride);

            return virtualCount == 0
                ? "No (no virtual members)"
                : "Limited (virtual members exist)";
        }

        private static string ResolveDocumentName(string? requestedName, SyntaxTree tree)
        {
            if (!string.IsNullOrWhiteSpace(requestedName)) return requestedName!;
            var fromPath = Path.GetFileName(tree.FilePath);
            return string.IsNullOrEmpty(fromPath) ? "(unnamed)" : fromPath;
        }

        private static (SyntaxTree Tree, MethodDeclarationSyntax Method)? FindMethodAcrossTrees(
            Compilation compilation, string methodName, string? documentName, CancellationToken ct)
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
                var candidates = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.Text == methodName)
                    .ToList();

                if (candidates.Count == 0) continue;

                var preferred = candidates.FirstOrDefault(m =>
                    m.Modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword)) && m.TypeParameterList == null)
                    ?? candidates[0];

                return (tree, preferred);
            }

            return null;
        }
    }
}