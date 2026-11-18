using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetAI.TestGenerator.Core.Extensions;

/// <summary>Roslyn helpers shared by the generator, the merger and the test-file reader.</summary>
public static class TestSyntaxExtensions
{
    private static readonly string[] TestAttributeSuffixes =
    {
        "Fact", "Theory", "Test", "TestCase", "TestMethod", "DataTestMethod", "StaFact", "STATestMethod"
    };

    /// <summary>Returns <c>true</c> if the method carries an xUnit/NUnit/MSTest test attribute.</summary>
    public static bool HasTestAttribute(this MethodDeclarationSyntax method)
    {
        foreach (var attributeList in method.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var name = attribute.Name.ToString();
                if (name.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    name = name.Substring(0, name.Length - "Attribute".Length);
                }

                if (TestAttributeSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A key that identifies a using directive independent of trivia (global/static/alias aware).</summary>
    public static string GetUsingKey(this UsingDirectiveSyntax usingDirective)
    {
        var normalized = usingDirective.WithoutTrivia();
        return string.Join("|", normalized.GlobalKeyword.RawKind, normalized.StaticKeyword.RawKind, normalized.Alias?.Name.ToString(),
            normalized.Name?.ToString());
    }

    /// <summary>A key that identifies a class member by kind and signature (used to de-duplicate merged members).</summary>
    public static string GetMemberKey(this MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => "M:" + m.GetMethodKey(),
        ConstructorDeclarationSyntax c => "C:" + (c.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static:" : string.Empty) +
                                          string.Join(",", c.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "")),
        FieldDeclarationSyntax f => "F:" + string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)),
        PropertyDeclarationSyntax p => "P:" + p.Identifier.Text,
        EventFieldDeclarationSyntax e => "E:" + string.Join(",", e.Declaration.Variables.Select(v => v.Identifier.Text)),
        BaseTypeDeclarationSyntax t => "T:" + t.Identifier.Text,
        _ => "O:" + member.NormalizeWhitespace().ToFullString()
    };

    /// <summary>Name + generic arity + parameter types/ref kinds.</summary>
    public static string GetMethodKey(this MethodDeclarationSyntax method)
    {
        var parts = new List<string> { method.Identifier.Text, (method.TypeParameterList?.Parameters.Count ?? 0).ToString() };

        foreach (var p in method.ParameterList.Parameters)
        {
            var refKind = p.Modifiers.FirstOrDefault(m =>
                m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword)).ValueText;

            parts.Add((p.Type?.WithoutTrivia().ToString() ?? string.Empty) + ":" + refKind);
        }

        return string.Join("|", parts);
    }
}
