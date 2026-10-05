namespace NetAI.TestGenerator.Core.Models;

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

    /// <summary>Gets whether the method returns <see cref="void" />.</summary>
    public bool ReturnsVoid { get; init; }

    /// <summary>Gets whether the method is an <c>async void</c> method.</summary>
    public bool IsAsyncVoid { get; init; }

    /// <summary>Gets facts about the method's containing type.</summary>
    public TypeFact ContainingType { get; init; } = new();

    /// <summary>Gets the method's parameters in declaration order.</summary>
    public List<ParameterFact> Parameters { get; init; } = new();

    /// <summary>Gets whether the method is virtual.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>Gets whether the method overrides a base member.</summary>
    public bool IsOverride { get; init; }

    /// <summary>Gets whether the method is abstract.</summary>
    public bool IsAbstract { get; init; }

    /// <summary>Gets whether the method is an iterator.</summary>
    public bool IsIterator { get; init; }

    /// <summary>Gets whether the method is an extension method.</summary>
    public bool IsExtension { get; init; }

    /// <summary>Gets or sets the returns task.</summary>
    public bool ReturnsTask { get; init; }

    /// <summary>Gets or sets the has cancellation token.</summary>
    public bool HasCancellationToken { get; init; }

    /// <summary>Gets or sets the generic parameters.</summary>
    public IReadOnlyList<string> GenericParameters { get; init; } = new List<string>();

    /// <summary>Gets or sets the attributes.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = new List<string>();

    /// <summary>Gets or sets the thrown exceptions.</summary>
    public IReadOnlyList<string> ThrownExceptions { get; init; } = new List<string>();
}