namespace NetAI.TestGenerator.Core.Models.Enums;

/// <summary>Describes how a referenced type is used inside the analyzed method.</summary>
[Flags]
public enum UsageKind
{
    /// <summary>The type is not used by the method.</summary>
    None = 0,

    /// <summary>The method reads a value of this type.</summary>
    Read = 1,

    /// <summary>The method writes a value of this type.</summary>
    Write = 2,

    /// <summary>The method calls a member of this type.</summary>
    Call = 4,

    /// <summary>The method creates an instance of this type.</summary>
    Create = 8,

    /// <summary>The method awaits a value of this type.</summary>
    Await = 16,

    /// <summary>The method throws this type.</summary>
    Throw = 32,

    /// <summary>The method references this type with <c>typeof</c>.</summary>
    TypeOf = 64,

    /// <summary>The method inherits from this type.</summary>
    Inherit = 128,

    /// <summary>The method uses this type as an attribute.</summary>
    Attribute = 256
}