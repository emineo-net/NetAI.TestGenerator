using Microsoft.CodeAnalysis;

namespace NetAI.TestGenerator.Core.Services;

/// <summary>Provides compiler diagnostics for generated C# source code.</summary>
public interface ICompilerService
{
    /// <summary>Compiles source code and returns the resulting diagnostics.</summary>
    /// <param name="sourceCode">C# source code to compile.</param>
    /// <param name="cancellationToken">Token used to cancel compilation.</param>
    /// <returns>The diagnostics reported by the compiler.</returns>
    Task<IReadOnlyList<Diagnostic>> CompileAndGetDiagnosticsAsync(string sourceCode, CancellationToken cancellationToken = default);
}