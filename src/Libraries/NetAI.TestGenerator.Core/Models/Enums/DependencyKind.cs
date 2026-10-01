namespace NetAI.TestGenerator.Core.Models.Enums;

/// <summary>Classifies a referenced type by how it can be substituted in a test.</summary>
public enum DependencyKind
{
    Interface,
    AbstractClass,
    ConcreteClass,
    SealedClass,
    StaticClass,
    Delegate,
    Struct,
    Enum,
    Primitive,
    Unknown,
}