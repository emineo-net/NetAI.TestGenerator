namespace NetAI.TestGenerator.Core.Models;

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