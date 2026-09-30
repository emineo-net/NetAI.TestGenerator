using System.Text;

namespace NetAI.TestGenerator.Core.Analysis;

public sealed class AnalyzerOptions
{
    public bool IncludeFrameworkTypes { get; init; } = true;

    public int MaxDiagnostics { get; init; } = 20;
}

public sealed class TestabilityReport
{
    public DateTimeOffset GeneratedAt { get; init; }
    public string DocumentName { get; init; } = "";
    public MethodFact Method { get; init; } = new();
    public List<TypeFact> ReferencedTypes { get; init; } = new();
    public string Verdict { get; init; } = "";
    public List<string> Recommendations { get; init; } = new();
    public List<string> CompilationErrors { get; init; } = new();

    public string ToPromptText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Roslyn-Faktenbericht (verbindlich – NICHT erfinden!)");
        sb.AppendLine();
        sb.AppendLine("Regeln:");
        sb.AppendLine("- Verwende ausschließlich die hier aufgeführten Typen.");
        sb.AppendLine("- Erfinde keine Interfaces, Properties oder usings.");
        sb.AppendLine("- Wenn die Testbarkeit eingeschränkt ist, gib eine Analyse statt Testcode aus.");
        sb.AppendLine();

        sb.AppendLine("## Methode");
        sb.AppendLine($"- Name:        {Method.Name}");
        sb.AppendLine($"- Signatur:    {Method.Signature}");
        sb.AppendLine($"- Rückgabe:    {Method.ReturnType}");
        sb.AppendLine($"- Zugriff:     {Method.Accessibility}");
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
        sb.AppendLine($"  - Zugriff:   {ct.Accessibility}");
        sb.AppendLine($"  - Mockable:  {ct.Mockable}");
        if (ct.Interfaces.Count > 0)
            sb.AppendLine($"  - Interfaces: {string.Join(", ", ct.Interfaces)}");
        sb.AppendLine();

        sb.AppendLine("## Referenzierte Typen");
        if (ReferencedTypes.Count == 0)
        {
            sb.AppendLine("(keine)");
        }
        else
        {
            foreach (var t in ReferencedTypes)
            {
                sb.AppendLine($"- {t.FullName}");
                sb.AppendLine($"    Kind={t.Kind}  Mockable={t.Mockable}  static={t.IsStatic}  " +
                              $"sealed={t.IsSealed}  statisch verwendet={t.UsedStatically}");
                if (t.Interfaces.Count > 0)
                    sb.AppendLine($"    Interfaces: {string.Join(", ", t.Interfaces)}");
                if (t.Constructors.Count > 0)
                    sb.AppendLine($"    Ctor: {string.Join(" | ", t.Constructors)}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Testbarkeit");
        sb.AppendLine(Verdict);
        sb.AppendLine();

        if (Recommendations.Count > 0)
        {
            sb.AppendLine("## Empfehlungen");
            foreach (var r in Recommendations)
                sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (CompilationErrors.Count > 0)
        {
            sb.AppendLine("## Compilerfehler im Projekt (Kontext)");
            foreach (var e in CompilationErrors)
                sb.AppendLine($"- {e}");
        }

        return sb.ToString();
    }
}

public sealed class MethodFact
{
    public string Name { get; init; } = "";
    public string Signature { get; init; } = "";
    public string ReturnType { get; init; } = "";
    public string Accessibility { get; init; } = "";
    public bool IsStatic { get; init; }
    public bool IsAsync { get; init; }
    public bool ReturnsVoid { get; init; }
    public bool IsAsyncVoid { get; init; }
    public TypeFact ContainingType { get; init; } = new();
    public List<ParameterFact> Parameters { get; init; } = new();
}

public sealed class ParameterFact
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public bool IsOptional { get; init; }
    public bool HasDefaultValue { get; init; }
    public string? DefaultValue { get; init; }
}

public sealed class TypeFact
{
    public string FullName { get; init; } = "";
    public string Namespace { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Accessibility { get; init; } = "";
    public bool IsStatic { get; init; }
    public bool IsSealed { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsInterface { get; init; }
    public string Mockable { get; init; } = "";
    public bool UsedStatically { get; set; }
    public List<string> Constructors { get; init; } = new();
    public List<string> Interfaces { get; init; } = new();
    public int VirtualMemberCount { get; init; }
    public int MemberCount { get; init; }
}