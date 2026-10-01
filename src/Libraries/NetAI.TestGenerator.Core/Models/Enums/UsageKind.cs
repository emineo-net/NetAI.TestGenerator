namespace NetAI.TestGenerator.Core.Models.Enums;

/// <summary>Describes how a referenced type is used inside the analyzed method.</summary>
[Flags]
public enum UsageKind
{
    None = 0,
    Read = 1,
    Write = 2,
    Call = 4,
    Create = 8,
    Await = 16,
    Throw = 32,
    TypeOf = 64,
    Inherit = 128,
    Attribute = 256,
}