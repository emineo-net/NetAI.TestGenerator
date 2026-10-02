using System.Text.RegularExpressions;
using DotNet10TestGenerator;
using Microsoft.CodeAnalysis;

namespace NetAI.TestGenerator.Core.Services;

internal sealed class TestProjectManagerCompilerService : ICompilerService
{
    private static readonly Regex ErrorLineRegex = new(@"(?<id>CS\d{4})\s*:\s*(?<msg>.+)$", RegexOptions.Compiled);

    private readonly Func<string, Task<TestGenerationResult>> _compile;

    public TestProjectManagerCompilerService(Func<string, Task<TestGenerationResult>> compile)
    {
        _compile = compile ?? throw new ArgumentNullException(nameof(compile));
    }

    public async Task<IReadOnlyList<Diagnostic>> CompileAndGetDiagnosticsAsync(string source, CancellationToken cancellationToken = default)
    {
        var result = await _compile(source);

        if (result?.CompilerErrors is null || result.CompilerErrors.Length == 0)
        {
            return Array.Empty<Diagnostic>();
        }

        var list = new List<Diagnostic>(result.CompilerErrors.Length);
        foreach (var err in result.CompilerErrors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            list.Add(ParseDiagnostic(err));
        }

        return list;
    }

    private static Diagnostic ParseDiagnostic(string error)
    {
        var text = error ?? string.Empty;
        var match = ErrorLineRegex.Match(text);

        var id = match.Success ? match.Groups["id"].Value : "NETAI0001";
        var message = match.Success ? match.Groups["msg"].Value : text;

        var descriptor = new DiagnosticDescriptor(id, "Compiler Error", "{0}", "Compiler", DiagnosticSeverity.Error, true);

        return Diagnostic.Create(descriptor, Location.None, message);
    }
}