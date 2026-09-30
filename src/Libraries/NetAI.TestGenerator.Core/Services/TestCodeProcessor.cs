using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using NetAI.TestGenerator.Core.Models.Enums;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NetAI.TestGenerator.Core.Services;

public class TestCodeProcessor
{
    private const int MaxFixIterations = 5;

    private readonly ICompilerService _compilerService;
    private readonly AdhocWorkspace _workspace;
    private readonly TestCodeBeautifier _beautifier;

    public TestCodeProcessor(ICompilerService compilerService)
    {
        _compilerService = compilerService
            ?? throw new ArgumentNullException(nameof(compilerService));

        _workspace = new AdhocWorkspace();
        _beautifier = new TestCodeBeautifier();
    }

    public async Task<string> ProcessTestClassAsync(
        string sourceCode,
        TestFramework testFramework,
        MockFramework mockFramework,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            throw new ArgumentException("Der Quellcode darf nicht leer sein.", nameof(sourceCode));

        string currentCode = await _beautifier.BeautifyAndAddUsingsAsync(
            sourceCode, testFramework, mockFramework, cancellationToken);

        IReadOnlyList<Diagnostic> diagnostics = await _compilerService
            .CompileAndGetDiagnosticsAsync(currentCode, cancellationToken);

        int iteration = 0;
        while (iteration < MaxFixIterations &&
               diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            iteration++;

            var tree = CSharpSyntaxTree.ParseText(
                currentCode, cancellationToken: cancellationToken);
            var root = (CompilationUnitSyntax)await tree.GetRootAsync(cancellationToken);

            bool anyFixed = false;

            foreach (var error in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                if (TryFixMissingUsing(root, error, testFramework, mockFramework, out var newRoot))
                {
                    root = newRoot;
                    anyFixed = true;
                }
            }

            if (!anyFixed)
                break;

            root = SortUsings(root);
            var doc = CreateDocumentFromRoot(root, cancellationToken);
            doc = await Formatter.FormatAsync(doc, cancellationToken: cancellationToken);
            root = (CompilationUnitSyntax)await doc.GetSyntaxRootAsync(cancellationToken);

            currentCode = root.ToFullString();

            diagnostics = await _compilerService
                .CompileAndGetDiagnosticsAsync(currentCode, cancellationToken);
        }

        return currentCode;
    }

    private static bool TryFixMissingUsing(
        CompilationUnitSyntax root,
        Diagnostic error,
        TestFramework testFramework,
        MockFramework mockFramework,
        out CompilationUnitSyntax updatedRoot)
    {
        updatedRoot = root;

        if (error.Severity != DiagnosticSeverity.Error)
            return false;

        if (error.Id != "CS0246" && error.Id != "CS0234" && error.Id != "CS0103")
            return false;

        string message = error.GetMessage();
        if (!TryExtractMissingName(error.Id, message, out string missingName))
            return false;

        missingName = NormalizeGenericName(missingName);

        string? ns = MapTypeToNamespace(
            missingName, error.Id, testFramework, mockFramework);

        if (string.IsNullOrEmpty(ns))
            return false;

        if (HasUsing(root, ns))
            return false;

        var usingDirective = SyntaxFactory
            .UsingDirective(SyntaxFactory.ParseName(ns))
            .NormalizeWhitespace();

        updatedRoot = root.AddUsings(usingDirective);
        return true;
    }

    private static bool TryExtractMissingName(
        string errorId,
        string message,
        out string missingName)
    {
        missingName = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        switch (errorId)
        {
            case "CS0246":
                {
                    var m = Regex.Match(
                        message,
                        @"type or namespace name '([^']+)' could not be found",
                        RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        missingName = m.Groups[1].Value;
                        return true;
                    }
                    break;
                }

            case "CS0234":
                {
                    var m = Regex.Match(
                        message,
                        @"type or namespace name '([^']+)' does not exist in the namespace '([^']+)'",
                        RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        missingName = m.Groups[2].Value + "." + m.Groups[1].Value;
                        return true;
                    }
                    break;
                }

            case "CS0103":
                {
                    var m = Regex.Match(
                        message,
                        @"The name '([^']+)' does not exist in the current context",
                        RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        missingName = m.Groups[1].Value;
                        return true;
                    }
                    break;
                }
        }

        return false;
    }

    private static string? MapTypeToNamespace(
        string missingName,
        string errorId,
        TestFramework testFramework,
        MockFramework mockFramework)
    {
        if (errorId == "CS0234" && missingName.Contains('.'))
            return missingName;

        switch (missingName)
        {
            case "Assert":
                return testFramework switch
                {
                    TestFramework.NUnit => "NUnit.Framework",
                    TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting",
                    _ => "Xunit",
                };

            case "Test":
                return testFramework switch
                {
                    TestFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting",
                    _ => "NUnit.Framework",
                };

            case "Fact":
            case "Theory":
            case "InlineData":
            case "MemberData":
            case "ClassData":
            case "IClassFixture":
            case "ICollectionFixture":
                return "Xunit";

            case "ITestOutputHelper":
                return "Xunit.Abstractions";

            case "TestFixture":
            case "SetUp":
            case "TearDown":
            case "OneTimeSetUp":
            case "OneTimeTearDown":
            case "TestCase":
            case "TestCaseSource":
            case "TestFixtureSetUp":
            case "TestFixtureTearDown":
                return "NUnit.Framework";

            case "TestClass":
            case "TestMethod":
            case "TestInitialize":
            case "TestCleanup":
            case "ClassInitialize":
            case "ClassCleanup":
            case "AssemblyInitialize":
            case "AssemblyCleanup":
            case "ExpectedException":
                return "Microsoft.VisualStudio.TestTools.UnitTesting";
        }

        switch (missingName)
        {
            case "Mock":
            case "It":
            case "Times":
            case "MockBehavior":
            case "MockRepository":
            case "MockSequence":
                return "Moq";

            case "Substitute":
            case "Arg":
            case "Received":
            case "Returns":
            case "CallInfo":
                return "NSubstitute";

            case "A":
            case "Fake":
            case "FakeOptions":
                return "FakeItEasy";
        }

        return missingName switch
        {
            "Task" or "ValueTask" or "TaskCompletionSource" => "System.Threading.Tasks",
            "CancellationToken" or "CancellationTokenSource" => "System.Threading",
            "List" or "Dictionary" or "IEnumerable" or "IReadOnlyList"
                or "IReadOnlyDictionary" or "IDictionary" or "IList"
                or "HashSet" or "ISet" or "ICollection" or "IReadOnlyCollection"
                or "KeyValuePair" or "Queue" or "Stack" or "LinkedList"
                => "System.Collections.Generic",
            "ImmutableArray" or "ImmutableList" or "ImmutableDictionary"
                or "ImmutableHashSet" => "System.Collections.Immutable",
            "Regex" or "Match" or "MatchCollection" => "System.Text.RegularExpressions",
            "StringBuilder" or "Encoding" => "System.Text",
            "JsonSerializer" or "JsonDocument" or "JsonElement"
                or "JsonSerializerOptions" or "JsonNamingPolicy" => "System.Text.Json",
            "JObject" or "JToken" or "JArray" or "JValue" => "Newtonsoft.Json.Linq",
            "JsonConvert" => "Newtonsoft.Json",
            "Debug" or "Trace" or "Stopwatch" or "Activity" => "System.Diagnostics",
            "BindingFlags" or "MemberInfo" or "MethodBase" or "MethodInfo"
                or "ConstructorInfo" or "PropertyInfo" or "FieldInfo" or "EventInfo"
                or "ParameterInfo" or "Assembly" or "Module" or "TargetException"
                or "TargetInvocationException" => "System.Reflection",
            "ILogger" or "ILoggerFactory" or "LogLevel" => "Microsoft.Extensions.Logging",
            "NullLogger" => "Microsoft.Extensions.Logging.Abstractions",
            "IOptions" or "IOptionsSnapshot" or "IOptionsMonitor" => "Microsoft.Extensions.Options",
            "IConfiguration" or "IConfigurationBuilder" => "Microsoft.Extensions.Configuration",
            "IServiceCollection" or "ServiceCollection" => "Microsoft.Extensions.DependencyInjection",
            "IServiceProvider" or "Guid" or "DateTime" or "DateTimeOffset"
                or "TimeSpan" or "Math" or "Console" or "Environment" or "Lazy"
                or "Func" or "Action" or "Predicate" or "ArgumentException"
                or "ArgumentNullException" or "InvalidOperationException"
                or "NotImplementedException" or "NotSupportedException"
                or "IDisposable" or "Nullable" or "Tuple" or "Uri" or "Random"
                or "Convert" or "StringComparison" or "Exception" or "Object"
                or "Type" or "Array" or "Comparer" or "EqualityComparer"
                => "System",
            _ => null,
        };
    }

    private static string NormalizeGenericName(string name)
    {
        int idx = name.IndexOf('<');
        return idx >= 0 ? name.Substring(0, idx) : name;
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
}