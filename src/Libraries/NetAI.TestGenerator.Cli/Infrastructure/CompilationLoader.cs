using Microsoft.CodeAnalysis;
using NetAI.TestGenerator.Core.Analysis;
using CoreAnalyzerOptions = NetAI.TestGenerator.Core.Models.AnalyzerOptions;

namespace NetAI.TestGenerator.Cli.Infrastructure;

public sealed class LoadedCompilation : IDisposable
{
    public LoadedCompilation(IDisposable? workspace, Compilation? compilation)
    {
        Workspace = workspace;
        Compilation = compilation;
    }

    public IDisposable? Workspace { get; }
    public Compilation? Compilation { get; }

    public void Dispose()
    {
        try { Workspace?.Dispose(); }
        catch { }
    }
}

public static class CompilationLoader
{
    /// <summary>
    /// Loads a Roslyn compilation for a single project via MSBuild.
    /// <paramref name="targetFile"/> is the file name the core uses to key the compilation;
    /// it must be a real file of the project (typically the first source to process).
    /// </summary>
    public static async Task<LoadedCompilation?> TryLoadAsync(
        string? projectPath,
        string? targetFile,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            return null;
        }

        try
        {
            log?.Invoke($"[NetAI] Loading project '{Path.GetFileName(projectPath)}' via MSBuild (once per run)…");

            var analyzer = new RoslynDllTestabilityAnalyzer(new CoreAnalyzerOptions());

            var loaded = await analyzer.LoadCompilationFromMsbuildAsync(
                null,
                projectPath,
                targetFile,
                log).ConfigureAwait(false);

            if (loaded.Compilation is null)
            {
                log?.Invoke("[NetAI] MSBuild returned no compilation; falling back to syntax-only.");
                try { loaded.Workspace?.Dispose(); } catch { }
                return null;
            }

            log?.Invoke($"[NetAI] Compilation loaded: {loaded.Compilation.SyntaxTrees.Count()} syntax trees.");
            return new LoadedCompilation(loaded.Workspace, loaded.Compilation);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[NetAI] MSBuild load failed: {ex.Message}; falling back to syntax-only.");
            return null;
        }
    }
}