using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace NetAI.TestGenerator.Cli.Infrastructure;

public static class SourceCollector
{
    private static readonly string[] ExcludedDirs = { "obj", "bin", ".git", ".vs", "node_modules", ".idea" };
    private static readonly string[] ExcludedSuffixes =
    {
        ".Designer.cs", ".g.cs", ".g.i.cs", ".AssemblyAttributes.cs", "AssemblyInfo.cs", "GlobalUsings.g.cs"
    };

    /// <summary>
    /// Resolves the list of source files to process.
    /// Priority: compilation.SyntaxTrees > projectPath (.csproj) > source path.
    /// </summary>
    public static IReadOnlyList<string> Collect(
        string? source,
        string? projectPath,
        Compilation? compilation,
        bool recursive,
        IReadOnlyList<string> filters,
        IReadOnlyList<string> excludes,
        int? maxFiles,
        Action<string>? log)
    {
        List<string> files;

        if (compilation is not null)
        {
            log?.Invoke($"[NetAI] Enumerating {compilation.SyntaxTrees.Count()} source file(s) from the loaded project compilation.");
            files = compilation.SyntaxTrees
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                .Select(p => Path.GetFullPath(p!))
                .ToList();
        }
        else if (!string.IsNullOrWhiteSpace(projectPath))
        {
            files = CollectFromProject(projectPath!, log);
        }
        else if (!string.IsNullOrWhiteSpace(source))
        {
            files = CollectFromPath(source!, recursive, log);
        }
        else
        {
            throw new InvalidOperationException("Provide a source file/directory or --project.");
        }

        files = files
            .Where(IsCandidate)
            .Where(f => MatchesAny(f, filters, emptyMeansAll: true))
            .Where(f => !MatchesAny(f, excludes, emptyMeansAll: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (maxFiles is int limit && files.Count > limit)
        {
            throw new InvalidOperationException(
                $"Found {files.Count} source files, --max-files is {limit}. Raise the limit or narrow the input.");
        }

        return files;
    }

    /// <summary>True if the file is a plausible C# source file (no generated/obj/bin).</summary>
    public static bool IsCandidate(string file)
    {
        var name = Path.GetFileName(file);
        if (ExcludedSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.Any(p => ExcludedDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private static List<string> CollectFromPath(string source, bool recursive, Action<string>? log)
    {
        if (File.Exists(source))
            return new List<string> { Path.GetFullPath(source) };

        if (!Directory.Exists(source))
            throw new FileNotFoundException($"Source path not found: {source}");

        var opts = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(source, "*.cs", opts)
            .Where(IsCandidate)
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static List<string> CollectFromProject(string projectPath, Action<string>? log)
    {
        var fullProject = Path.GetFullPath(projectPath);
        if (!File.Exists(fullProject))
            throw new FileNotFoundException($"Project not found: {fullProject}");

        var projectDir = Path.GetDirectoryName(fullProject)!;
        var doc = XDocument.Load(fullProject);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var explicitIncludes = doc.Descendants(ns + "Compile")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Replace('\\', Path.DirectorySeparatorChar))
            .Where(v => !v.Contains('*', StringComparison.Ordinal))
            .Select(v => Path.GetFullPath(Path.Combine(projectDir, v)))
            .Where(File.Exists)
            .ToList();

        if (explicitIncludes.Count > 0)
            return explicitIncludes;

        log?.Invoke("[NetAI] No explicit <Compile Include> entries; falling back to a project-directory scan.");
        return Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(IsCandidate)
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static bool MatchesAny(string file, IReadOnlyList<string> globs, bool emptyMeansAll)
    {
        if (globs.Count == 0) return emptyMeansAll;

        var name = Path.GetFileName(file);
        foreach (var glob in globs)
        {
            var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }
}