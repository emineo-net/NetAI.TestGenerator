using Microsoft.CodeAnalysis;

namespace NetAI.TestGenerator.Core.Services;

public interface ICompilerService
{
    Task<IReadOnlyList<Diagnostic>> CompileAndGetDiagnosticsAsync(
        string sourceCode,
        CancellationToken cancellationToken = default);
}