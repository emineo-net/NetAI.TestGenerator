namespace NetAI.TestGenerator.Core.Models;

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