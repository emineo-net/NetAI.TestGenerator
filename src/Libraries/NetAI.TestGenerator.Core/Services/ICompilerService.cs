using Microsoft.CodeAnalysis;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Provides compiler diagnostics for generated C# source code.</summary>
public interface ICompilerService
{

    /// <summary>Compiles the source and returns compiler diagnostics.</summary>
    Task<IReadOnlyList<Diagnostic>> CompileAndGetDiagnosticsAsync(string sourceCode, CancellationToken cancellationToken = default);
}