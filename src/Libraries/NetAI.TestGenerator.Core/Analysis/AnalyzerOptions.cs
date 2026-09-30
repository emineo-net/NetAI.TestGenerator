using System.Text;

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>Controls which framework types and compilation diagnostics appear in testability reports.</summary>
public sealed class AnalyzerOptions
{
    /// <summary>Gets whether framework types such as <c>System.String</c> are included in analysis results.</summary>
    public bool IncludeFrameworkTypes { get; init; } = true;

    /// <summary>Gets the maximum number of compilation diagnostics included in a report.</summary>
    public int MaxDiagnostics { get; init; } = 20;
}

/// <summary>Summarizes a method's testability, referenced types, and relevant compilation diagnostics.</summary>
public sealed class TestabilityReport
{
    /// <summary>Gets the UTC time at which the report was generated.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the name of the source document analyzed.</summary>
    public string DocumentName { get; init; } = "";

    /// <summary>Gets the analyzed method and its containing type.</summary>
    public MethodFact Method { get; init; } = new();

    /// <summary>Gets the non-framework and framework types referenced by the method.</summary>
    public List<TypeFact> ReferencedTypes { get; init; } = new();

    /// <summary>Gets the analyzer's concise testability verdict.</summary>
    public string Verdict { get; init; } = "";

    /// <summary>Gets recommendations for improving the method's testability.</summary>
    public List<string> Recommendations { get; init; } = new();

    /// <summary>Gets compilation errors captured as context for the analysis.</summary>
    public List<string> CompilationErrors { get; init; } = new();

    /// <summary>Formats the report as prompt-ready text, including rules that prevent invented code facts.</summary>
    /// <returns>A plain-text representation of the report.</returns>
    public string ToPromptText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Roslyn Facts Report (authoritative - DO NOT invent!)");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use only the types listed here.");
        sb.AppendLine("- Do not invent interfaces, properties, or using directives.");
        sb.AppendLine("- If testability is limited, provide an analysis instead of test code.");
        sb.AppendLine();

        sb.AppendLine("## Method");
        sb.AppendLine($"- Name:        {Method.Name}");
        sb.AppendLine($"- Signature:   {Method.Signature}");
        sb.AppendLine($"- Return type: {Method.ReturnType}");
        sb.AppendLine($"- Accessibility: {Method.Accessibility}");
        sb.AppendLine($"- static:      {Method.IsStatic}");
        sb.AppendLine($"- async:       {Method.IsAsync}");
        sb.AppendLine($"- async void:  {Method.IsAsyncVoid}");
        sb.AppendLine();

        if (Method.Parameters.Count > 0)
        {
            sb.AppendLine("Parameter:");
            foreach (var p in Method.Parameters)
            {
                var def = p.IsOptional ? $" (optional = {p.DefaultValue ?? "null"})" : "";
                sb.AppendLine($"  - {p.Type} {p.Name}{def}");
            }
            sb.AppendLine();
        }

        var ct = Method.ContainingType;
        sb.AppendLine($"Containing Type: {ct.FullName}");
        sb.AppendLine($"  - Kind:      {ct.Kind}");
        sb.AppendLine($"  - Accessibility: {ct.Accessibility}");
        sb.AppendLine($"  - Mockable:  {ct.Mockable}");
        if (ct.Interfaces.Count > 0)
            sb.AppendLine($"  - Interfaces: {string.Join(", ", ct.Interfaces)}");
        sb.AppendLine();

        sb.AppendLine("## Referenced types");
        if (ReferencedTypes.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var t in ReferencedTypes)
            {
                sb.AppendLine($"- {t.FullName}");
                sb.AppendLine($"    Kind={t.Kind}  Mockable={t.Mockable}  static={t.IsStatic}  " +
                              $"sealed={t.IsSealed}  used statically={t.UsedStatically}");
                if (t.Interfaces.Count > 0)
                    sb.AppendLine($"    Interfaces: {string.Join(", ", t.Interfaces)}");
                if (t.Constructors.Count > 0)
                    sb.AppendLine($"    Ctor: {string.Join(" | ", t.Constructors)}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Testability");
        sb.AppendLine(Verdict);
        sb.AppendLine();

        if (Recommendations.Count > 0)
        {
            sb.AppendLine("## Recommendations");
            foreach (var r in Recommendations)
                sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (CompilationErrors.Count > 0)
        {
            sb.AppendLine("## Project compiler errors (context)");
            foreach (var e in CompilationErrors)
                sb.AppendLine($"- {e}");
        }

        return sb.ToString();
    }
}

/// <summary>Describes the signature, modifiers, parameters, and containing type of an analyzed method.</summary>
public sealed class MethodFact
{
    /// <summary>Gets the method's name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets the method's display signature.</summary>
    public string Signature { get; init; } = "";

    /// <summary>Gets the method's return type.</summary>
    public string ReturnType { get; init; } = "";

    /// <summary>Gets the method's declared accessibility.</summary>
    public string Accessibility { get; init; } = "";

    /// <summary>Gets whether the method is static.</summary>
    public bool IsStatic { get; init; }

    /// <summary>Gets whether the method is declared with <c>async</c>.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Gets whether the method returns <see cref="void"/>.</summary>
    public bool ReturnsVoid { get; init; }

    /// <summary>Gets whether the method is an <c>async void</c> method.</summary>
    public bool IsAsyncVoid { get; init; }

    /// <summary>Gets facts about the method's containing type.</summary>
    public TypeFact ContainingType { get; init; } = new();

    /// <summary>Gets the method's parameters in declaration order.</summary>
    public List<ParameterFact> Parameters { get; init; } = new();
}

/// <summary>Describes a parameter on an analyzed method.</summary>
public sealed class ParameterFact
{
    /// <summary>Gets the parameter's name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets the parameter's type.</summary>
    public string Type { get; init; } = "";

    /// <summary>Gets whether the parameter can be omitted at the call site.</summary>
    public bool IsOptional { get; init; }

    /// <summary>Gets whether the parameter declares an explicit default value.</summary>
    public bool HasDefaultValue { get; init; }

    /// <summary>Gets the default value's display text, or <see langword="null"/> when none is declared.</summary>
    public string? DefaultValue { get; init; }
}

/// <summary>Describes a type referenced by a method and the analyzer's basic mockability facts.</summary>
public sealed class TypeFact
{
    /// <summary>Gets the type's fully qualified display name.</summary>
    public string FullName { get; init; } = "";

    /// <summary>Gets the type's namespace.</summary>
    public string Namespace { get; init; } = "";

    /// <summary>Gets the Roslyn type kind, such as <c>Class</c> or <c>Interface</c>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Gets the type's declared accessibility.</summary>
    public string Accessibility { get; init; } = "";

    /// <summary>Gets whether the type is static.</summary>
    public bool IsStatic { get; init; }

    /// <summary>Gets whether the type is sealed.</summary>
    public bool IsSealed { get; init; }

    /// <summary>Gets whether the type is abstract.</summary>
    public bool IsAbstract { get; init; }

    /// <summary>Gets whether the type is an interface.</summary>
    public bool IsInterface { get; init; }

    /// <summary>Gets a human-readable indication of whether the type can be mocked.</summary>
    public string Mockable { get; init; } = "";

    /// <summary>Gets or sets whether the method uses this type through a static member.</summary>
    public bool UsedStatically { get; set; }

    /// <summary>Gets the type's public instance constructors.</summary>
    public List<string> Constructors { get; init; } = new();

    /// <summary>Gets the interfaces implemented by the type.</summary>
    public List<string> Interfaces { get; init; } = new();

    /// <summary>Gets the number of virtual, abstract, or overridden members on the type.</summary>
    public int VirtualMemberCount { get; init; }

    /// <summary>Gets the number of members declared on the type.</summary>
    public int MemberCount { get; init; }
}