using DotNet10TestGenerator;
using Microsoft.CodeAnalysis;
using NetAI.TestGenerator.Core.Services;
using System.Text.RegularExpressions;
using NetAI.TestGenerator.Core.Models;

namespace NetAI.TestGenerator.Core.Services;

internal sealed class TestProjectManagerCompilerService : ICompilerService
{
    private readonly Func<string, Task<TestGenerationResult>> _compile;

    public TestProjectManagerCompilerService(
        Func<string, Task<TestGenerationResult>> compile)
    {
        _compile = compile ?? throw new ArgumentNullException(nameof(compile));
    }

    public async Task<IReadOnlyList<Diagnostic>> CompileAndGetDiagnosticsAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        var result = await _compile(source);

        if (result?.CompilerErrors is null || result.CompilerErrors.Length == 0)
            return Array.Empty<Diagnostic>();

        var list = new List<Diagnostic>(result.CompilerErrors.Length);
        foreach (var err in result.CompilerErrors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            list.Add(ParseDiagnostic(err));
        }
        return list;
    }

    private static readonly Regex ErrorLineRegex = new(
        @"(?<id>CS\d{4})\s*:\s*(?<msg>.+)$",
        RegexOptions.Compiled);

    private static Diagnostic ParseDiagnostic(string error)
    {
        var text = error ?? string.Empty;
        var match = ErrorLineRegex.Match(text);

        string id = match.Success ? match.Groups["id"].Value : "NETAI0001";
        string message = match.Success ? match.Groups["msg"].Value : text;

        var descriptor = new DiagnosticDescriptor(
            id,
            "Compiler Error",
            "{0}",
            "Compiler",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        return Diagnostic.Create(descriptor, Location.None, message);
    }
}