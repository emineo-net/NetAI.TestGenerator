using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Core.Models.Enums;

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>Generates test skeletons and prompt context from Roslyn method symbols.</summary>
public static class UnitTestSkeletonGenerator
{
    /// <summary>Specifies how a test skeleton is constructed.</summary>
    public enum SkeletonMode
    {
        /// <summary>Constructs the subject using its dependencies.</summary>
        ConstructorInjection,

        /// <summary>Constructs the subject without dependencies.</summary>
        Parameterless,

        /// <summary>Skips construction for static or abstract types.</summary>
        StaticOrAbstract,

        /// <summary>Requires refactoring before a test can be generated.</summary>
        RefactorFirst,

        /// <summary>Generates a skipped test.</summary>
        Skip,

        /// <summary>Uses a fallback skeleton.</summary>
        Fallback
    }

    /// <summary>Specifies how the generated test exercises or skips a method.</summary>
    public enum TestStrategy
    {
        /// <summary>Method is testable; a normal test is generated.</summary>
        Direct,

        /// <summary>Private (but otherwise testable) method; accessed via reflection.</summary>
        Reflection,

        /// <summary>Method must be refactored first; skip test plus comment block.</summary>
        RefactorFirst,

        /// <summary>Method should be skipped; pure skip test.</summary>
        Skip
    }

    /// <summary>Generates the requested output.</summary>
    public static GeneratorResult GenerateFromMethod(IMethodSymbol method, TestFrameworkProfile profile, bool requiresSta = false,
        TestStrategy strategy = TestStrategy.Direct, IReadOnlyList<string>? refactoringLines = null, string? skipReason = null)
    {
        try
        {
            var containingType = method.ContainingType;
            if (containingType is null)
            {
                return CreateTotalFallback("No containing type found.", strategy);
            }

            var ns = containingType.ContainingNamespace?.IsGlobalNamespace == false
                ? containingType.ContainingNamespace.ToDisplayString()
                : "YourProject";

            var className = containingType.Name;
            var sutTypeName = containingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var testClassName = className + "Tests";
            var targetMethodName = method.Name;

            // Decide async-ness by the return type: a non-async method returning Task must still be awaited.
            var isAwaitable = IsTaskLike(method.ReturnType);

            if (strategy is TestStrategy.RefactorFirst or TestStrategy.Skip)
            {
                return CreateSkipStyleSkeleton(ns, className, sutTypeName, testClassName, targetMethodName, isAwaitable, profile,
                    requiresSta, strategy, refactoringLines ?? Array.Empty<string>(), skipReason);
            }

            if (IsGenericOrNestedInGeneric(containingType))
            {
                return CreateTotalFallback(
                    $"Type '{sutTypeName}' is generic; choose concrete type arguments for the SUT (e.g. `{className}<object>`).",
                    strategy);
            }

            if (method.IsStatic)
            {
                return CreateStaticOrAbstract(ns, className, sutTypeName, testClassName, targetMethodName, isAwaitable,
                    method.ReturnsVoid, profile, requiresSta, strategy);
            }

            if (containingType.IsAbstract || containingType.IsStatic)
            {
                return CreateTotalFallback(
                    $"Type '{sutTypeName}' is abstract; instance method '{targetMethodName}' needs a concrete test subclass " +
                    "or a partial mock (CallBase) as SUT.", strategy);
            }

            var ctorParams = GetConstructorParameters(containingType);
            if (ctorParams is null)
            {
                return CreateTotalFallback(
                    $"Type '{sutTypeName}' has no public constructor; create the SUT through its factory or another accessible API.",
                    strategy);
            }

            if (ctorParams.Count == 0)
            {
                return CreateParameterless(ns, className, sutTypeName, testClassName, targetMethodName, isAwaitable, method.ReturnsVoid,
                    profile, requiresSta, strategy);
            }

            return CreateWithConstructorInjection(containingType, ctorParams, ns, sutTypeName, testClassName, targetMethodName,
                isAwaitable, method.ReturnsVoid, profile, requiresSta, strategy);
        }
        catch (Exception ex)
        {
            return CreateTotalFallback($"Critical error during skeleton generation: {ex.Message}", strategy);
        }
    }

    /// <summary>Generates the requested output.</summary>
    public static GeneratorResult Generate(string sourceCode, Compilation compilation)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(sourceCode);
            var updated = compilation.AddSyntaxTrees(tree);
            var model = updated.GetSemanticModel(tree);

            var classDecl = tree.GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            if (classDecl is null)
            {
                return CreateTotalFallback("No class found in the provided source code.", TestStrategy.Direct);
            }

            var symbol = model.GetDeclaredSymbol(classDecl);
            if (symbol is null)
            {
                return CreateTotalFallback("No class symbol found.", TestStrategy.Direct);
            }

            var firstMethod = classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Modifiers.Any(SyntaxKind.PublicKeyword));

            var methodSymbol = firstMethod is null ? null : model.GetDeclaredSymbol(firstMethod);

            methodSymbol ??= symbol.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m =>
                m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public);

            if (methodSymbol is null)
            {
                return CreateTotalFallback("No public method found.", TestStrategy.Direct);
            }

            var profile = TestFrameworkProfile.Create(TestFramework.xUnit, MockFramework.Moq);

            return GenerateFromMethod(methodSymbol, profile);
        }
        catch (Exception ex)
        {
            return CreateTotalFallback($"Critical error during Roslyn analysis: {ex.Message}", TestStrategy.Direct);
        }
    }

    /// <summary>Returns the parameters of the richest public constructor, or <see langword="null" /> when none is public.</summary>
    private static List<IParameterSymbol>? GetConstructorParameters(INamedTypeSymbol type)
    {
        var best = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).Where(c => !c.IsStatic)
            .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();

        if (best is null)
        {
            return null;
        }

        return best.Parameters.ToList();
    }

    /// <summary>True for Task, Task&lt;T&gt;, ValueTask and ValueTask&lt;T&gt;.</summary>
    internal static bool IsTaskLike(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { Name: "Task" or "ValueTask" } named &&
               named.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
    }

    /// <summary>True when the type or any of its containing types has type parameters.</summary>
    internal static bool IsGenericOrNestedInGeneric(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for interfaces and non-sealed classes whose behavior a mock can actually replace.</summary>
    private static bool IsMockable(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface)
        {
            return true;
        }

        if (type.TypeKind != TypeKind.Class || type.IsSealed || type.IsStatic || type.SpecialType != SpecialType.None)
        {
            return false;
        }

        return type.IsAbstract || type.GetMembers().Any(m => (m.IsVirtual || m.IsAbstract) && !m.IsSealed);
    }

    /// <summary>Simple value for a constructor parameter that is not mocked.</summary>
    private static string DefaultValueFor(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return "string.Empty";
        }

        return type.IsValueType ? "default" : "default!";
    }

    private static GeneratorResult CreateSkipStyleSkeleton(string ns, string className, string sutTypeName, string testClassName,
        string methodName, bool isAsync, TestFrameworkProfile profile, bool requiresSta, TestStrategy strategy,
        IReadOnlyList<string> refactoringLines, string? skipReason)
    {
        var mode = strategy == TestStrategy.RefactorFirst ? SkeletonMode.RefactorFirst : SkeletonMode.Skip;

        var reason = skipReason ?? (strategy == TestStrategy.RefactorFirst
            ? "requires production-code refactoring"
            : "method is not directly testable");

        var skipAttr = profile.SkipAttributeFor(reason, requiresSta);

        var prompt = new StringBuilder();
        prompt.AppendLine("=== Test Setup ===");
        prompt.AppendLine(profile.ToPromptHeader());
        prompt.AppendLine($"Requires STA thread: {(requiresSta ? "yes" : "no")}");
        prompt.AppendLine($"Strategy: {strategy}");
        prompt.AppendLine();
        prompt.AppendLine($"SUT: `{sutTypeName}`");
        prompt.AppendLine($"Method: `{methodName}`");
        prompt.AppendLine($"Skip reason: {reason}");
        prompt.AppendLine();

        if (strategy == TestStrategy.RefactorFirst)
        {
            prompt.AppendLine("You MUST NOT write a real test body.");
            prompt.AppendLine("Inside the AI AREA, list the required source refactorings as comments,");
            prompt.AppendLine("based on <SuggestedRefactoringPattern> and <Recommendations> from <SemanticAnalysis>.");
            prompt.AppendLine("Keep the Skip attribute EXACTLY as shown in the skeleton.");
        }
        else
        {
            prompt.AppendLine("You MUST NOT write a real test body.");
            prompt.AppendLine("Inside the AI AREA, only explain WHY the test is skipped.");
            prompt.AppendLine("Keep the Skip attribute EXACTLY as shown in the skeleton.");
        }

        prompt.AppendLine();

        var usings = new List<string>(profile.RequiredNamespaces(SkeletonPurpose.SkipTest));
        var usingsBlock = string.Join(Environment.NewLine, usings.Select(u => $"using {u};"));

        var classAttr = profile.ClassAttribute is { } ca ? ca + Environment.NewLine : string.Empty;

        var refactoringBlock = string.Empty;
        if (strategy == TestStrategy.RefactorFirst && refactoringLines.Count > 0)
        {
            var rb = new StringBuilder();
            rb.AppendLine("    // =========================================================================");
            rb.AppendLine("    // REQUIRED SOURCE REFACTORING (do not implement the test yet):");
            rb.AppendLine("    // =========================================================================");
            for (var i = 0; i < refactoringLines.Count; i++)
            {
                rb.AppendLine($"    // {i + 1}) {refactoringLines[i]}");
            }

            rb.AppendLine("    // =========================================================================");
            rb.AppendLine();
            refactoringBlock = rb.ToString();
        }

        var testMethodName = strategy == TestStrategy.RefactorFirst
            ? $"{methodName}_RequiresRefactoring"
            : $"{methodName}_IsNotDirectlyTestable";

        var aiAreaHint = strategy == TestStrategy.RefactorFirst
            ? "// AI AREA: Describe the required refactoring below (comments only, no real test)."
            : "// AI AREA: Explain below why this method is skipped.";

        var skeleton = $@"{usingsBlock}

namespace {ns}.UnitTests;

{classAttr}public class {testClassName}
{{
{refactoringBlock}    {skipAttr}
    public void {testMethodName}()
    {{
        {aiAreaHint}
    }}
}}";

        return new GeneratorResult(skeleton, prompt.ToString().TrimEnd(), mode, strategy, testClassName, ns, sutTypeName, methodName,
            requiresSta);
    }

    private static GeneratorResult CreateWithConstructorInjection(INamedTypeSymbol containingType, List<IParameterSymbol> ctorParams,
        string ns, string sutTypeName, string testClassName, string methodName, bool isAsync, bool returnsVoid,
        TestFrameworkProfile profile, bool requiresSta, TestStrategy strategy)
    {
        var mockFields = new List<string>();
        var mockInits = new List<string>();
        var sutArgs = new List<string>();
        var prompt = new StringBuilder();

        prompt.AppendLine("=== Test Setup ===");
        prompt.AppendLine(profile.ToPromptHeader());
        prompt.AppendLine($"Requires STA thread: {(requiresSta ? "yes" : "no")}");
        prompt.AppendLine($"Strategy: {strategy}");
        prompt.AppendLine();

        prompt.AppendLine("=== SUT ===");
        prompt.AppendLine($"Type:   `{sutTypeName}`");
        prompt.AppendLine($"Method: `{methodName}`");
        prompt.AppendLine($"Async:  {(isAsync ? "yes" : "no")} (returns void: {returnsVoid})");
        prompt.AppendLine($"Namespace for test: `{ns}.UnitTests`");
        if (strategy == TestStrategy.Reflection)
        {
            prompt.AppendLine("Access: private -> use reflection to invoke the method.");
        }

        prompt.AppendLine();

        string? firstMockName = null;
        if (profile.HasMockFramework)
        {
            foreach (var p in ctorParams)
            {
                var pName = p.Name;
                var fieldName = $"_{pName}";
                var typeName = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

                if (!IsMockable(p.Type))
                {
                    // Value types, strings and sealed/non-virtual classes cannot be mocked: pass a plain value instead.
                    mockFields.Add($"    private readonly {typeName} {fieldName};");
                    mockInits.Add($"        {fieldName} = {DefaultValueFor(p.Type)}; // TODO: provide value");
                    sutArgs.Add(fieldName);
                    prompt.AppendLine($"=== Value `{fieldName}` ===");
                    prompt.AppendLine($"Type: `{typeName}` (not mockable) - provide a real value.");
                    prompt.AppendLine();
                    continue;
                }

                firstMockName ??= fieldName;
                mockFields.Add(profile.FieldDeclaration(typeName, fieldName));
                mockInits.Add(profile.FieldInitialization(typeName, fieldName));
                sutArgs.Add(profile.ConstructorArgument(fieldName));

                AppendMockMethodsForPrompt(p.Type, fieldName, profile, prompt);
            }
        }
        else
        {
            prompt.AppendLine("=== Dependencies (no mocking framework configured) ===");
            prompt.AppendLine("You must provide a real value or null for every constructor parameter.");

            foreach (var p in ctorParams)
            {
                var pName = p.Name;
                var typeName = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var fieldName = $"_{pName}";

                mockFields.Add($"    private readonly {typeName} {fieldName}; // TODO: provide value");
                mockInits.Add($"        {fieldName} = default!; // TODO: provide value");
                sutArgs.Add(fieldName);

                prompt.AppendLine($"- `{fieldName}` (type `{typeName}`)");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine("=== Assertion example ===");
        prompt.AppendLine($"  {profile.AssertionExample}");
        prompt.AppendLine();

        if (profile.HasMockFramework && firstMockName is not null)
        {
            prompt.AppendLine("=== Setup example (fill placeholders) ===");
            prompt.AppendLine($"  {profile.SetupExample(firstMockName, "MethodName(...)")}");
            prompt.AppendLine();
        }

        var code = BuildSkeletonStructure(ns, testClassName, sutTypeName, profile, mockFields, mockInits, string.Join(", ", sutArgs),
            methodName, isAsync, returnsVoid, requiresSta);

        return new GeneratorResult(code, prompt.ToString().TrimEnd(), SkeletonMode.ConstructorInjection, strategy, testClassName, ns,
            sutTypeName, methodName, requiresSta);
    }

    private static GeneratorResult CreateParameterless(string ns, string className, string sutTypeName, string testClassName,
        string methodName, bool isAsync, bool returnsVoid, TestFrameworkProfile profile, bool requiresSta, TestStrategy strategy)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("=== Test Setup ===");
        prompt.AppendLine(profile.ToPromptHeader());
        prompt.AppendLine($"Requires STA thread: {(requiresSta ? "yes" : "no")}");
        prompt.AppendLine($"Strategy: {strategy}");
        prompt.AppendLine();
        prompt.AppendLine($"SUT `{className}` has no constructor with parameters; no mocks are needed.");
        prompt.AppendLine($"Assertion example: {profile.AssertionExample}");

        var code = BuildSkeletonStructure(ns, testClassName, sutTypeName, profile, new List<string>(), new List<string>(), string.Empty,
            methodName, isAsync, returnsVoid, requiresSta);

        return new GeneratorResult(code, prompt.ToString().TrimEnd(), SkeletonMode.Parameterless, strategy, testClassName, ns, sutTypeName,
            methodName, requiresSta);
    }

    private static GeneratorResult CreateStaticOrAbstract(string ns, string className, string sutTypeName, string testClassName,
        string methodName, bool isAsync, bool returnsVoid, TestFrameworkProfile profile, bool requiresSta, TestStrategy strategy)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("=== Test Setup ===");
        prompt.AppendLine(profile.ToPromptHeader());
        prompt.AppendLine($"Requires STA thread: {(requiresSta ? "yes" : "no")}");
        prompt.AppendLine($"Strategy: {strategy}");
        prompt.AppendLine();
        prompt.AppendLine($"Method `{sutTypeName}.{methodName}` is static; no instance of `{className}` is needed.");
        prompt.AppendLine($"Call `{sutTypeName}.{methodName}(...)` directly.");
        prompt.AppendLine($"Assertion example: {profile.AssertionExample}");

        var usings = string.Join(Environment.NewLine, profile.RequiredNamespaces().Select(u => $"using {u};"));

        var asyncTest = isAsync && !returnsVoid;
        var methodSig = asyncTest
            ? $"public async Task {methodName}_WhenCalled_ShouldBehavior()"
            : $"public void {methodName}_WhenCalled_ShouldBehavior()";

        var classAttr = profile.ClassAttribute is { } ca ? ca + Environment.NewLine : string.Empty;

        var skeleton = $@"{usings}

namespace {ns}.UnitTests;

{classAttr}public class {testClassName}
{{
    // `{sutTypeName}.{methodName}` is static; no instance is needed.
    // Call the method directly via `{sutTypeName}.{methodName}(...)`.

    {profile.FactAttribute(requiresSta)}
    {methodSig}
    {{
        // =========================================================================
        // AI AREA
        // =========================================================================

        // Arrange

        // Act
        // var result = {sutTypeName}.{methodName}(...);

        // Assert

        // =========================================================================
    }}
}}";

        return new GeneratorResult(skeleton, prompt.ToString().TrimEnd(), SkeletonMode.StaticOrAbstract, strategy, testClassName, ns,
            sutTypeName, methodName, requiresSta);
    }

    private static GeneratorResult CreateTotalFallback(string errorMessage, TestStrategy strategy)
    {
        var skeleton = $@"using FluentAssertions;
using Xunit;

namespace YourProject.UnitTests;

public class AutomatedTests
{{
    // {errorMessage}
    // Fallback activated: please create the instantiation and mocks entirely yourself.

    [Fact]
    public void Test_Placeholder()
    {{
        // AI AREA
    }}
}}";

        return new GeneratorResult(skeleton, $"WARNING: {errorMessage}. You have to write the entire test yourself from scratch.",
            SkeletonMode.Fallback, strategy, "AutomatedTests", "YourProject", "object", "Execute", false);
    }

    private static string BuildSkeletonStructure(string ns, string testClassName, string sutTypeName, TestFrameworkProfile profile,
        List<string> fields, List<string> inits, string args, string methodName, bool isAsync, bool returnsVoid, bool requiresSta)
    {
        var usings = string.Join(Environment.NewLine, profile.RequiredNamespaces().Select(u => $"using {u};"));

        var classAttr = profile.ClassAttribute is { } ca ? ca + Environment.NewLine : string.Empty;

        string setupBlock;
        if (profile.UsesConstructorForSetup)
        {
            var initsJoined = inits.Count > 0 ? string.Join(Environment.NewLine, inits) + Environment.NewLine : string.Empty;

            setupBlock = $@"    public {testClassName}()
    {{
{initsJoined}        _sut = new {sutTypeName}({args});
    }}";
        }
        else
        {
            var initsJoined = inits.Count > 0 ? string.Join(Environment.NewLine, inits) + Environment.NewLine : string.Empty;

            setupBlock = $@"    {profile.SetupAttribute}
    public void SetUp()
    {{
{initsJoined}        _sut = new {sutTypeName}({args});
    }}";
        }

        var fieldsBlock = fields.Count > 0 ? string.Join(Environment.NewLine, fields) + Environment.NewLine : string.Empty;

        var asyncTest = isAsync && !returnsVoid;
        var methodSig = asyncTest
            ? $"public async Task {methodName}_WhenCalled_ShouldBehavior()"
            : $"public void {methodName}_WhenCalled_ShouldBehavior()";

        return $@"{usings}

namespace {ns}.UnitTests;

{classAttr}public class {testClassName}
{{
{fieldsBlock}    private readonly {sutTypeName} _sut;

{setupBlock}

    {profile.FactAttribute(requiresSta)}
    {methodSig}
    {{
        // =========================================================================
        // AI AREA: Only the content between the markers is filled in by the AI.
        // =========================================================================

        // Arrange

        // Act

        // Assert

        // =========================================================================
    }}
}}";
    }

    private static void AppendMockMethodsForPrompt(ITypeSymbol typeSymbol, string fieldName, TestFrameworkProfile profile, StringBuilder sb)
    {
        sb.AppendLine($"=== Mock `{fieldName}` ===");
        sb.AppendLine($"Type: `{typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}`");
        sb.AppendLine("Setup example:");
        sb.AppendLine($"  {profile.SetupExample(fieldName, "MethodName(...)")}");
        sb.AppendLine("Available methods:");

        var allMembers = typeSymbol.GetMembers().Concat(typeSymbol.AllInterfaces.SelectMany(i => i.GetMembers()));

        var methods = allMembers.OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary)
            .Where(m => m.DeclaredAccessibility == Accessibility.Public).ToList();

        if (methods.Count == 0)
        {
            sb.AppendLine("- (no public methods found)");
            sb.AppendLine();
            return;
        }

        foreach (var method in methods)
        {
            var sig = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            sb.AppendLine($"  - {sig}");
        }

        sb.AppendLine();
    }

    /// <summary>Contains a generated test skeleton and its prompt context.</summary>
    public sealed record GeneratorResult(
        string TestSkeleton,
        string AiPromptContext,
        SkeletonMode Mode,
        TestStrategy Strategy,
        string TestClassName,
        string Namespace,
        string SutTypeName,
        string TargetMethodName,
        bool RequiresSta);
}