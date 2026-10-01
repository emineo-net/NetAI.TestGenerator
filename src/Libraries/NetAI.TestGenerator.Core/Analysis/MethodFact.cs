namespace NetAI.TestGenerator.Core.Analysis;

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