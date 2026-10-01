using System.Text;

namespace NetAI.TestGenerator.Core.Models;

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


    // NEW:
    public bool IsDirectlyTestable { get; init; }
    public IReadOnlyList<string> Blockers { get; init; } = new List<string>();
    public IReadOnlyList<string> AnalyzedCallGraph { get; init; } = new List<string>();




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