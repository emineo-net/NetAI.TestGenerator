using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>
///     Generates a complete test-class snippet for a method whose behavior can
///     be inferred statically, without calling the LLM. Handles:
///     - parameterless methods returning a compile-time constant
///     - parameterless methods that throw unconditionally
///     - parameterless void methods with an empty body
///     Methods marked with NotImplementedException are signalled as "do not test".
/// </summary>
internal static class TrivialTestGenerator
{
    /// <summary>
    ///     <c>TestSnippet</c>: a full class snippet ready for merging, or null when
    ///     the method is not trivial and should fall through to the LLM.
    ///     <c>ShouldSkipEntirely</c>: true when the method should not be tested at all
    ///     (currently only for <c>NotImplementedException</c> throw-only methods).
    /// </summary>
    public sealed record Result(string? TestSnippet, bool ShouldSkipEntirely);

    public static Result TryGenerate(
        MethodDeclarationSyntax method,
        IMethodSymbol symbol,
        SemanticModel model,
        TestFrameworkProfile profile,
        bool requiresSta)
    {
        // Only handle parameterless methods. Parameters would need valid argument
        // values, which we cannot infer statically — fall through to the LLM.
        if (method.ParameterList.Parameters.Count > 0)
        {
            return new Result(null, false);
        }

        var expressionBody = method.ExpressionBody;
        var body = method.Body;

        // Expression-bodied method: `Type M() => <expr>;`
        if (expressionBody is not null)
        {
            if (expressionBody.Expression is ThrowExpressionSyntax throwExpr)
            {
                return HandleThrow(symbol, throwExpr.Expression, profile, requiresSta);
            }

            var constant = model.GetConstantValue(expressionBody.Expression);
            if (constant.HasValue)
            {
                return new Result(
                    BuildConstantReturnTest(symbol, constant.Value, profile, requiresSta),
                    false);
            }

            return new Result(null, false);
        }

        if (body is null)
        {
            return new Result(null, false);
        }

        // Empty void method → smoke test.
        if (body.Statements.Count == 0 && symbol.ReturnsVoid)
        {
            return new Result(BuildEmptyVoidTest(symbol, profile, requiresSta), false);
        }

        if (body.Statements.Count != 1)
        {
            return new Result(null, false);
        }

        var statement = body.Statements[0];

        if (statement is ThrowStatementSyntax throwStmt && throwStmt.Expression is not null)
        {
            return HandleThrow(symbol, throwStmt.Expression, profile, requiresSta);
        }

        if (statement is ReturnStatementSyntax returnStmt && returnStmt.Expression is not null)
        {
            var constant = model.GetConstantValue(returnStmt.Expression);
            if (constant.HasValue)
            {
                return new Result(
                    BuildConstantReturnTest(symbol, constant.Value, profile, requiresSta),
                    false);
            }
        }

        return new Result(null, false);
    }

    // ------------------------------------------------------------------ throw handling

    private static Result HandleThrow(
        IMethodSymbol symbol,
        ExpressionSyntax throwExpression,
        TestFrameworkProfile profile,
        bool requiresSta)
    {
        // NotImplementedException / NotSupportedException → not a test target.
        if (throwExpression is ObjectCreationExpressionSyntax creation)
        {
            var typeName = creation.Type.ToString();
            if (typeName.EndsWith("NotImplementedException", StringComparison.Ordinal)
                || typeName.EndsWith("NotSupportedException", StringComparison.Ordinal))
            {
                return new Result(null, true);
            }
        }

        // Try to extract the thrown exception type for the assertion.
        string? exceptionTypeName = null;
        if (throwExpression is ObjectCreationExpressionSyntax objCreation)
        {
            exceptionTypeName = objCreation.Type.ToString();
        }
        else if (throwExpression is IdentifierNameSyntax identifier)
        {
            // `throw someException;` — we cannot statically know the type without
            // resolving the identifier; fall through with a generic assertion.
            exceptionTypeName = null;
        }

        return new Result(
            BuildThrowTest(symbol, exceptionTypeName, profile, requiresSta),
            false);
    }

    // ------------------------------------------------------------------ builders

    private static string BuildConstantReturnTest(
        IMethodSymbol symbol,
        object? constantValue,
        TestFrameworkProfile profile,
        bool requiresSta)
    {
        var className = symbol.ContainingType.Name;
        var testClassName = className + "Tests";
        var testMethodName = $"{symbol.Name}_WhenCalled_ShouldReturnExpected";
        var literal = FormatConstant(constantValue, symbol.ReturnType);

        var sutCall = symbol.IsStatic
            ? $"{className}.{symbol.Name}()"
            : $"_sut.{symbol.Name}()";

        var assertion = profile.UseFluentAssertions
            ? $"result.Should().Be({literal});"
            : $"Assert.Equal({literal}, result);";

        return BuildClassSnippet(
            className, testClassName, symbol.ContainingNamespace, profile, requiresSta,
            extraMembers: null,
            methodAttributes: profile.FactAttribute(requiresSta),
            methodName: testMethodName,
            methodSignature: "public void " + testMethodName + "()",
            methodBody: $"        var result = {sutCall};\n        {assertion}",
            isStaticSut: symbol.IsStatic);
    }

    private static string BuildThrowTest(
        IMethodSymbol symbol,
        string? exceptionTypeName,
        TestFrameworkProfile profile,
        bool requiresSta)
    {
        var className = symbol.ContainingType.Name;
        var testClassName = className + "Tests";
        var testMethodName = $"{symbol.Name}_WhenCalled_ShouldThrow";
        var target = exceptionTypeName ?? "Exception";

        var sutCall = symbol.IsStatic
            ? $"{className}.{symbol.Name}"
            : $"_sut.{symbol.Name}";

        var assertion = profile.UseFluentAssertions
            ? $"act.Should().Throw<{target}>();"
            : $"Assert.Throws<{target}>(act);";

        var body =
            $"        Action act = () => {sutCall}();\n" +
            $"        {assertion}";

        // Ensure `using System;` for Action.
        var usingsOverride = new[] { "System" };

        return BuildClassSnippet(
            className, testClassName, symbol.ContainingNamespace, profile, requiresSta,
            extraMembers: null,
            methodAttributes: profile.FactAttribute(requiresSta),
            methodName: testMethodName,
            methodSignature: "public void " + testMethodName + "()",
            methodBody: body,
            isStaticSut: symbol.IsStatic,
            additionalUsingNamespaces: usingsOverride);
    }

    private static string BuildEmptyVoidTest(
        IMethodSymbol symbol,
        TestFrameworkProfile profile,
        bool requiresSta)
    {
        var className = symbol.ContainingType.Name;
        var testClassName = className + "Tests";
        var testMethodName = $"{symbol.Name}_WhenCalled_ShouldNotThrow";

        var sutCall = symbol.IsStatic
            ? $"{className}.{symbol.Name}"
            : $"_sut.{symbol.Name}";

        var assertion = profile.UseFluentAssertions
            ? "act.Should().NotThrow();"
            : "Assert.Null(Record.Exception(act));";

        var body =
            $"        Action act = () => {sutCall}();\n" +
            $"        {assertion}";

        var usingsOverride = new[] { "System" };

        return BuildClassSnippet(
            className, testClassName, symbol.ContainingNamespace, profile, requiresSta,
            extraMembers: null,
            methodAttributes: profile.FactAttribute(requiresSta),
            methodName: testMethodName,
            methodSignature: "public void " + testMethodName + "()",
            methodBody: body,
            isStaticSut: symbol.IsStatic,
            additionalUsingNamespaces: usingsOverride);
    }

    // ------------------------------------------------------------------ snippet assembly

    private static string BuildClassSnippet(
        string sutClassName,
        string testClassName,
        INamespaceSymbol containingNamespace,
        TestFrameworkProfile profile,
        bool requiresSta,
        string? extraMembers,
        string methodAttributes,
        string methodName,
        string methodSignature,
        string methodBody,
        bool isStaticSut,
        string[]? additionalUsingNamespaces = null)
    {
        var ns = containingNamespace.IsGlobalNamespace
            ? "YourProject.UnitTests"
            : containingNamespace.ToDisplayString() + ".UnitTests";

        var usingNamespaces = new System.Collections.Generic.List<string>(profile.RequiredNamespaces());
        if (additionalUsingNamespaces is not null)
        {
            foreach (var extrnsa in additionalUsingNamespaces)
            {
                if (!usingNamespaces.Contains(extrnsa, StringComparer.Ordinal))
                {
                    usingNamespaces.Add(extrnsa);
                }
            }
        }

        var usings = string.Join(Environment.NewLine,
            usingNamespaces.Select(u => $"using {u};"));

        var classAttribute = profile.ClassAttribute is { } ca ? ca + Environment.NewLine : string.Empty;

        // For static methods we do not need the _sut field/ctor.
        var sutBlock = isStaticSut
            ? string.Empty
            : $@"    private readonly {sutClassName} _sut;

    public {testClassName}()
    {{
        _sut = new {sutClassName}();
    }}

";

        var extra = string.IsNullOrWhiteSpace(extraMembers) ? string.Empty : extraMembers;

        return $@"{usings}

namespace {ns};

{classAttribute}public class {testClassName}
{{
{sutBlock}{extra}    {methodAttributes}
    {methodSignature}
    {{
{methodBody}
    }}
}}";
    }

    // ------------------------------------------------------------------ literal formatting

    private static string FormatConstant(object? value, ITypeSymbol returnType)
    {
        if (value is null)
        {
            return "null";
        }

        // Enum: `model.GetConstantValue` gives the underlying integral value; we
        // prefer the named member for readability.
        if (returnType.TypeKind == TypeKind.Enum && returnType is INamedTypeSymbol enumType)
        {
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.HasConstantValue && Equals(member.ConstantValue, value))
                {
                    return $"{enumType.Name}.{member.Name}";
                }
            }

            return $"({enumType.Name}){Convert.ToString(value, CultureInfo.InvariantCulture)}";
        }

        return value switch
        {
            bool b => b ? "true" : "false",
            string s => "\"" + s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t") + "\"",
            char c => "'" + (c == '\'' ? "\\'" : c == '\\' ? "\\\\" : c.ToString()) + "'",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
            uint ui => ui.ToString(CultureInfo.InvariantCulture) + "U",
            int i => i.ToString(CultureInfo.InvariantCulture),
            short sh => sh.ToString(CultureInfo.InvariantCulture),
            byte by => by.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "default"
        };
    }
}