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
    /// <summary>
    /// Rein syntaxbasierter Testbarkeits-Analysator – netstandard2.0-kompatibel.
    ///
    /// Verzichtet bewusst auf Compilation / SemanticModel / Workspace, weil
    /// diese in unbekannten Kundenprojekten (Monolithen, Multi-Targeting,
    /// WPF, Source Generators) oft nicht zuverlässig aufgebaut werden können.
    ///
    /// Konsequenz:
    ///   - Struktur-Analyse (Modifier, Parameter, async void, static) exakt.
    ///   - Abhängigkeits-Analyse heuristisch (PascalCase-MemberAccess,
    ///     Static-Whitelist, Interface-Namenskonvention I*).
    ///   - Mockability nur als Näherung (keine echte Vererbungsprüfung).
    ///   - Compiler-Diagnostics: nur Syntaxfehler aus dem SyntaxTree.
    /// </summary>
    public sealed class RoslynSyntaxTestabilityAnalyzer
    {
        private readonly AnalyzerOptions _options;

        public RoslynSyntaxTestabilityAnalyzer(AnalyzerOptions? options = null)
            => _options = options ?? new AnalyzerOptions();

        // ==================================================================
        //  Static-Whitelist: bekannte BCL-Typen mit statischer Nutzung,
        //  die typischerweise Testbarkeits-Blocker sind.
        // ==================================================================
        private static readonly HashSet<string> KnownStaticClasses = new(StringComparer.Ordinal)
        {
            // System.IO
            "File", "Directory", "Path", "FileStream", "FileInfo", "DirectoryInfo",
            // System
            "Console", "Environment", "Math", "Convert", "Guid",
            "DateTime", "DateTimeOffset", "TimeSpan", "Random", "Uri",
            "GC", "Activator", "AppDomain", "Array", "Nullable", "Type",
            // System.Diagnostics
            "Debug", "Trace", "Activity", "Stopwatch",
            // System.Text.RegularExpressions
            "Regex",
            // System.Text
            "Encoding",
            // System.Threading
            "Thread", "ThreadPool", "Interlocked",
            // System.Threading.Tasks
            "Task",
            // Serialisierung
            "JsonConvert", "JsonSerializer", "JsonDocument", "JObject",
        };

        // ==================================================================
        //  Bekannte BCL-Kurznamen (ohne Namespace geschrieben).
        // ==================================================================
        private static readonly HashSet<string> CommonBclTypes = new(StringComparer.Ordinal)
        {
            "Task", "ValueTask", "CancellationToken", "CancellationTokenSource",
            "List", "Dictionary", "IEnumerable", "IReadOnlyList", "IReadOnlyDictionary",
            "IDictionary", "IList", "HashSet", "ISet", "ICollection",
            "IReadOnlyCollection", "KeyValuePair", "Queue", "Stack", "LinkedList",
            "ImmutableArray", "ImmutableList", "ImmutableDictionary", "ImmutableHashSet",
            "Regex", "Match", "MatchCollection",
            "StringBuilder", "Encoding",
            "JsonSerializer", "JsonDocument", "JsonElement", "JsonSerializerOptions",
            "JObject", "JToken", "JArray", "JValue", "JsonConvert",
            "Debug", "Trace", "Stopwatch", "Activity",
            "ILogger", "ILoggerFactory", "LogLevel",
            "IOptions", "IOptionsSnapshot", "IOptionsMonitor",
            "IConfiguration", "IConfigurationBuilder",
            "IServiceCollection", "ServiceCollection", "IServiceProvider",
            "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "Math", "Console",
            "Environment", "Lazy", "Func", "Action", "Predicate",
            "ArgumentException", "ArgumentNullException", "InvalidOperationException",
            "NotImplementedException", "NotSupportedException", "IDisposable",
            "Nullable", "Tuple", "Uri", "Random", "Convert", "StringComparison",
            "Exception", "Object", "Type", "Array", "Comparer", "EqualityComparer",
        };

        // ==================================================================
        //  Public API – Datei / Quelltext / SyntaxTree
        // ==================================================================

        public async Task<TestabilityReport> AnalyzeFileAsync(
            string filePath,
            string methodName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Dateipfad darf nicht leer sein.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Quelldatei nicht gefunden.", filePath);

            var sourceCode = await Task.Run(() => File.ReadAllText(filePath), ct)
                .ConfigureAwait(false);
            var documentName = Path.GetFileName(filePath);

            return await AnalyzeSourceCodeAsync(sourceCode, documentName, methodName, ct)
                .ConfigureAwait(false);
        }

        public Task<TestabilityReport> AnalyzeSourceCodeAsync(
            string sourceCode,
            string documentName,
            string methodName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceCode))
                throw new ArgumentException("Quellcode darf nicht leer sein.", nameof(sourceCode));

            var tree = CSharpSyntaxTree.ParseText(
                sourceCode,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: documentName,
                cancellationToken: ct);

            return AnalyzeTreeAsync(tree, documentName, methodName, ct);
        }

        public Task<TestabilityReport> AnalyzeTreeAsync(
            SyntaxTree tree,
            string documentName,
            string methodName,
            CancellationToken ct = default)
        {
            if (tree is null) throw new ArgumentNullException(nameof(tree));

            var root = tree.GetCompilationUnitRoot(ct);
            var report = AnalyzeMethodInRoot(documentName, root, methodName, ct, tree);
            return Task.FromResult(report);
        }

        // ==================================================================
        //  Public API – Verzeichnis (Batch)
        // ==================================================================

        public async Task<IReadOnlyList<TestabilityReport>> AnalyzeDirectoryAsync(
            string directory,
            string searchPattern = "*.cs",
            bool recursive = true,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Verzeichnis darf nicht leer sein.", nameof(directory));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(directory, searchPattern, option);

            var results = new List<TestabilityReport>();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                try
                {
                    var code = await Task.Run(() => File.ReadAllText(file), ct)
                        .ConfigureAwait(false);
                    var tree = CSharpSyntaxTree.ParseText(code, path: file, cancellationToken: ct);
                    var root = tree.GetCompilationUnitRoot(ct);

                    foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                    {
                        var report = AnalyzeMethodInRoot(
                            Path.GetFileName(file), root, method, ct, tree);
                        results.Add(report);
                    }
                }
                catch
                {
                    // Datei überspringen
                }
            }

            return results;
        }

        // ==================================================================
        //  Interne Analyse
        // ==================================================================

        private TestabilityReport AnalyzeMethodInRoot(
            string documentName,
            CompilationUnitSyntax root,
            string methodName,
            CancellationToken ct,
            SyntaxTree tree)
        {
            var method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName)
                ?? throw new InvalidOperationException(
                    $"Methode '{methodName}' wurde im Dokument nicht gefunden.");

            return AnalyzeMethodInRoot(documentName, root, method, ct, tree);
        }

        private TestabilityReport AnalyzeMethodInRoot(
            string documentName,
            CompilationUnitSyntax root,
            MethodDeclarationSyntax method,
            CancellationToken ct,
            SyntaxTree tree)
        {
            var containingType = method.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault();

            var methodFact = BuildMethodFactSyntax(method, containingType);

            var localNames = CollectLocalNames(method);
            var typeFacts = CollectReferencedTypesSyntax(method, localNames);

            var verdict = EvaluateTestability(methodFact, typeFacts);
            var recs = BuildRecommendations(methodFact, typeFacts);

            // Syntaxfehler (nicht Compilerfehler!) aus dem Tree
            var syntaxErrors = tree.GetDiagnostics(ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(_options.MaxDiagnostics)
                .Select(d => d.ToString())
                .ToList();

            return new TestabilityReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                DocumentName = documentName,
                Method = methodFact,
                ReferencedTypes = typeFacts,
                Verdict = verdict,
                Recommendations = recs,
                CompilationErrors = syntaxErrors,
            };
        }

        // ==================================================================
        //  MethodFact aus Syntax
        // ==================================================================

        private static MethodFact BuildMethodFactSyntax(
            MethodDeclarationSyntax method,
            TypeDeclarationSyntax? containingType)
        {
            bool isStatic = HasModifier(method.Modifiers, SyntaxKind.StaticKeyword);
            bool isAsync = HasModifier(method.Modifiers, SyntaxKind.AsyncKeyword);
            bool isPublic = HasModifier(method.Modifiers, SyntaxKind.PublicKeyword);
            bool isPrivate = HasModifier(method.Modifiers, SyntaxKind.PrivateKeyword);
            bool isProtected = HasModifier(method.Modifiers, SyntaxKind.ProtectedKeyword);
            bool isInternal = HasModifier(method.Modifiers, SyntaxKind.InternalKeyword);

            string accessibility =
                isPublic ? "Public" :
                isPrivate ? "Private" :
                isProtected ? "Protected" :
                isInternal ? "Internal" :
                "Private";

            bool returnsVoid =
                method.ReturnType is PredefinedTypeSyntax predef &&
                predef.Keyword.IsKind(SyntaxKind.VoidKeyword);

            var parameters = method.ParameterList.Parameters
                .Select(p => new ParameterFact
                {
                    Name = p.Identifier.Text,
                    Type = p.Type?.ToString() ?? string.Empty,
                    IsOptional = p.Default != null,
                    HasDefaultValue = p.Default != null,
                    DefaultValue = p.Default?.Value.ToString(),
                })
                .ToList();

            var containingFact = BuildContainingTypeFact(containingType);

            return new MethodFact
            {
                Name = method.Identifier.Text,
                Signature = BuildSignature(method),
                ReturnType = method.ReturnType.ToString(),
                Accessibility = accessibility,
                IsStatic = isStatic,
                IsAsync = isAsync,
                ReturnsVoid = returnsVoid,
                IsAsyncVoid = isAsync && returnsVoid,
                ContainingType = containingFact,
                Parameters = parameters,
            };
        }

        private static TypeFact BuildContainingTypeFact(TypeDeclarationSyntax? type)
        {
            if (type is null)
            {
                return new TypeFact
                {
                    FullName = "(unbekannt)",
                    Namespace = string.Empty,
                    Kind = "Unknown",
                    Accessibility = "Internal",
                    Mockable = "Unbekannt (kein Containing Type gefunden)",
                    UsedStatically = false,
                    Interfaces = new List<string>(),
                    Constructors = new List<string>(),
                };
            }

            bool isInterface = type is InterfaceDeclarationSyntax;
            bool isStatic = HasModifier(type.Modifiers, SyntaxKind.StaticKeyword);
            bool isSealed = HasModifier(type.Modifiers, SyntaxKind.SealedKeyword);
            bool isAbstract = HasModifier(type.Modifiers, SyntaxKind.AbstractKeyword);
            bool isPublic = HasModifier(type.Modifiers, SyntaxKind.PublicKeyword);

            string kind = type switch
            {
                InterfaceDeclarationSyntax => "Interface",
                ClassDeclarationSyntax => "Class",
                StructDeclarationSyntax => "Struct",
                RecordDeclarationSyntax => "Record",
                _ => "Unknown",
            };

            var nsDecl = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            string ns = nsDecl?.Name.ToString() ?? string.Empty;

            string fullName = string.IsNullOrEmpty(ns)
                ? type.Identifier.Text
                : ns + "." + type.Identifier.Text;

            var interfaces = type.BaseList?.Types
                .Select(t => t.Type.ToString())
                .Where(LooksLikeInterfaceName)
                .ToList() ?? new List<string>();

            var ctors = type.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Where(c => HasModifier(c.Modifiers, SyntaxKind.PublicKeyword))
                .Select(c =>
                    c.Identifier.Text + "(" +
                    string.Join(", ", c.ParameterList.Parameters
                        .Select(p => p.Type?.ToString() ?? "?")) + ")")
                .ToList();

            int virtualMemberCount = type.Members
                .OfType<MethodDeclarationSyntax>()
                .Count(m =>
                    HasModifier(m.Modifiers, SyntaxKind.VirtualKeyword) ||
                    HasModifier(m.Modifiers, SyntaxKind.AbstractKeyword) ||
                    HasModifier(m.Modifiers, SyntaxKind.OverrideKeyword));

            int memberCount = type.Members.Count;

            return new TypeFact
            {
                FullName = fullName,
                Namespace = ns,
                Kind = kind,
                Accessibility = isPublic ? "Public" : "Internal",
                IsStatic = isStatic,
                IsSealed = isSealed,
                IsAbstract = isAbstract,
                IsInterface = isInterface,
                Mockable = ComputeMockabilitySyntax(isInterface, isStatic, isSealed, isAbstract, virtualMemberCount),
                UsedStatically = false,
                Interfaces = interfaces,
                Constructors = ctors,
                VirtualMemberCount = virtualMemberCount,
                MemberCount = memberCount,
            };
        }

        // ==================================================================
        //  Referenzierte Typen – rein syntaktische Heuristik
        // ==================================================================

        private List<TypeFact> CollectReferencedTypesSyntax(
            MethodDeclarationSyntax method,
            HashSet<string> localNames)
        {
            var seen = new Dictionary<string, TypeFact>(StringComparer.Ordinal);

            void Add(string? typeName, bool staticUse)
            {
                if (string.IsNullOrWhiteSpace(typeName)) return;

                var display = typeName!.Trim();
                var lookup = StripGenericArgs(display);
                if (string.IsNullOrEmpty(lookup)) return;

                if (IsFrameworkType(lookup) && !_options.IncludeFrameworkTypes && !staticUse)
                    return;

                if (seen.TryGetValue(display, out var existing))
                {
                    if (staticUse) existing.UsedStatically = true;
                    return;
                }

                seen[display] = new TypeFact
                {
                    FullName = display,
                    Namespace = GuessNamespace(display),
                    Kind = GuessKind(lookup),
                    Accessibility = "Unbekannt (Syntax)",
                    IsStatic = KnownStaticClasses.Contains(lookup),
                    IsSealed = false,
                    IsAbstract = false,
                    IsInterface = LooksLikeInterfaceName(lookup),
                    Mockable = GuessMockability(lookup),
                    UsedStatically = staticUse,
                    Interfaces = new List<string>(),
                    Constructors = new List<string>(),
                };
            }

            foreach (var node in method.DescendantNodes())
            {
                switch (node)
                {
                    case ObjectCreationExpressionSyntax oc:
                        Add(ExtractTypeNameFromCreation(oc), staticUse: false);
                        break;

                    case MemberAccessExpressionSyntax ma:
                        HandleMemberAccessReceiver(ma, localNames, Add);
                        break;

                    case VariableDeclarationSyntax vd:
                        if (!vd.Type.IsVar)
                            Add(vd.Type.ToString(), staticUse: false);
                        break;
                }
            }

            return seen.Values
                .OrderBy(t => t.Namespace.StartsWith("System", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
        }

        private static void HandleMemberAccessReceiver(
            MemberAccessExpressionSyntax ma,
            HashSet<string> localNames,
            Action<string?, bool> add)
        {
            var expr = ma.Expression;

            switch (expr)
            {
                case IdentifierNameSyntax id:
                    {
                        var name = id.Identifier.Text;
                        if (name is "this" or "base") return;
                        if (localNames.Contains(name)) return;
                        if (!LooksLikeTypeName(name)) return;

                        add(name,  true);
                        break;
                    }

                case GenericNameSyntax gen:
                    {
                        var name = gen.Identifier.Text;
                        if (localNames.Contains(name)) return;
                        if (!LooksLikeTypeName(name)) return;

                        add(name,  true);
                        break;
                    }
            }
        }

        private static string ExtractTypeNameFromCreation(ObjectCreationExpressionSyntax oc)
        {
            return oc.Type switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                GenericNameSyntax gen => gen.Identifier.Text,
                QualifiedNameSyntax q => q.ToString(),
                _ => oc.Type.ToString(),
            };
        }

        private static HashSet<string> CollectLocalNames(MethodDeclarationSyntax method)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var p in method.ParameterList.Parameters)
                names.Add(p.Identifier.Text);

            foreach (var vd in method.DescendantNodes().OfType<VariableDeclarationSyntax>())
                foreach (var v in vd.Variables)
                    names.Add(v.Identifier.Text);

            foreach (var fe in method.DescendantNodes().OfType<ForEachStatementSyntax>())
                names.Add(fe.Identifier.Text);

            foreach (var cs in method.DescendantNodes().OfType<CatchDeclarationSyntax>())
                if (cs.Identifier.Text.Length > 0)
                    names.Add(cs.Identifier.Text);

            return names;
        }

        // ==================================================================
        //  Bewertung (unverändert zur semantischen Variante)
        // ==================================================================

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
                blockers.Add($"Mögliche statische Abhängigkeit auf '{t.FullName}' – nicht mockbar.");

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

            var statics = types
                .Where(t => t.UsedStatically && !t.IsInterface)
                .ToList();

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

        // ==================================================================
        //  Hilfs-Heuristiken
        // ==================================================================

        private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind)
            => modifiers.Any(m => m.IsKind(kind));

        private static string BuildSignature(MethodDeclarationSyntax method)
        {
            var returnType = method.ReturnType.ToString();
            var name = method.Identifier.Text;
            var parameters = string.Join(", ",
                method.ParameterList.Parameters.Select(p =>
                    (p.Type?.ToString() ?? "?") + " " + p.Identifier.Text));
            return $"{returnType} {name}({parameters})";
        }

        private static string StripGenericArgs(string name)
        {
            int idx = name.IndexOf('<');
            return idx >= 0 ? name.Substring(0, idx) : name;
        }

        private static bool LooksLikeTypeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsUpper(name[0])) return false;
            return true;
        }

        private static bool LooksLikeInterfaceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length < 2) return false;
            return name[0] == 'I' && char.IsUpper(name[1]);
        }

        private static bool IsFrameworkType(string name)
        {
            if (name.StartsWith("System.", StringComparison.Ordinal)) return true;
            if (name.StartsWith("Microsoft.", StringComparison.Ordinal)) return true;
            return CommonBclTypes.Contains(name);
        }

        private static string GuessNamespace(string typeName)
        {
            if (typeName.Contains('.'))
            {
                int lastDot = typeName.LastIndexOf('.');
                return typeName.Substring(0, lastDot);
            }

            if (CommonBclTypes.Contains(typeName)) return "System";
            return string.Empty;
        }

        private static string GuessKind(string shortName)
        {
            if (LooksLikeInterfaceName(shortName)) return "Interface";
            if (KnownStaticClasses.Contains(shortName)) return "Class (static, vermutet)";
            return "Unknown (Syntax)";
        }

        private static string GuessMockability(string shortName)
        {
            if (LooksLikeInterfaceName(shortName)) return "Wahrscheinlich ja (Interface-Namenskonvention)";
            if (KnownStaticClasses.Contains(shortName)) return "Nein (static)";
            return "Unbekannt (Syntax, keine Symbolauflösung)";
        }

        private static string ComputeMockabilitySyntax(
            bool isInterface, bool isStatic, bool isSealed, bool isAbstract, int virtualCount)
        {
            if (isInterface) return "Ja (Interface)";
            if (isStatic) return "Nein (static)";
            if (isSealed) return "Nein (sealed)";
            if (isAbstract) return "Ja (abstract)";
            return virtualCount == 0
                ? "Nein (keine virtuellen Member)"
                : "Eingeschränkt (virtuelle Member vorhanden)";
        }
    }
}