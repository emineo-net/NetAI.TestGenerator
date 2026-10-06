namespace NetAI.TestGenerator.Core.Models.Enums;

/// <summary>Classifies a referenced type by how it can be substituted in a test.</summary>
public enum DependencyKind
{
    /// <summary>The dependency is an interface.</summary>
    Interface,

    /// <summary>The dependency is an abstract class.</summary>
    AbstractClass,

    /// <summary>The dependency is a concrete class.</summary>
    ConcreteClass,

    /// <summary>The dependency is a sealed class.</summary>
    SealedClass,

    /// <summary>The dependency is a static class.</summary>
    StaticClass,

    /// <summary>The dependency is a delegate.</summary>
    Delegate,

    /// <summary>The dependency is a structure.</summary>
    Struct,

    /// <summary>The dependency is an enumeration.</summary>
    Enum,

    /// <summary>The dependency is a primitive type.</summary>
    Primitive,

    /// <summary>The dependency kind is unknown.</summary>
    Unknown
}