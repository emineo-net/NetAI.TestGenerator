using System.IO.Enumeration;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using NetAI.TestGenerator.Core.Infrastructure;

namespace NetAI.TestGenerator.Cli.Infrastructure;

public static class SourceCollector
{
    /// <summary>
    /// Resolves the list of source files to process.
    /// Files come from the compilation, the project (.csproj) or the source path – in that order. When both a project and a
    /// source path are given, the source path narrows the project's files (the project still provides the compilation).
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
        string? baseDirectory = null;

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
            baseDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath!));
            files = CollectFromProject(projectPath!, log);
        }
        else if (!string.IsNullOrWhiteSpace(source))
        {
            files = CollectFromPath(source!, recursive);
        }
        else
        {
            throw new InvalidOperationException("Provide a source file/directory or --project.");
        }

        if ((compilation is not null || !string.IsNullOrWhiteSpace(projectPath)) && !string.IsNullOrWhiteSpace(source))
        {
            files = files.Where(f => IsWithin(f, source!, recursive)).ToList();
        }

        files = files
            .Where(f => SourceFileFilter.IsCandidate(f, baseDirectory))
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
    public static bool IsCandidate(string file) => SourceFileFilter.IsCandidate(file);

    private static bool IsWithin(string file, string source, bool recursive)
    {
        var fullSource = Path.GetFullPath(source);
        if (File.Exists(fullSource))
            return string.Equals(file, fullSource, StringComparison.OrdinalIgnoreCase);

        var directory = fullSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            return false;

        return recursive || string.Equals(Path.GetDirectoryName(file) + Path.DirectorySeparatorChar, directory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CollectFromPath(string source, bool recursive)
    {
        if (File.Exists(source))
            return new List<string> { Path.GetFullPath(source) };

        if (!Directory.Exists(source))
            throw new FileNotFoundException($"Source path not found: {source}");

        var opts = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var baseDirectory = Path.GetFullPath(source);
        return Directory.EnumerateFiles(source, "*.cs", opts)
            .Select(Path.GetFullPath)
            .Where(f => SourceFileFilter.IsCandidate(f, baseDirectory))
            .ToList();
    }

    /// <summary>
    /// Approximates the SDK's default Compile glob: all *.cs below the project directory (without nested projects),
    /// plus explicitly included (linked) files, minus explicit &lt;Compile Remove&gt; entries.
    /// </summary>
    private static List<string> CollectFromProject(string projectPath, Action<string>? log)
    {
        var fullProject = Path.GetFullPath(projectPath);
        if (!File.Exists(fullProject))
            throw new FileNotFoundException($"Project not found: {fullProject}");

        var projectDir = Path.GetDirectoryName(fullProject)!;
        var doc = XDocument.Load(fullProject);

        List<string> ResolveItems(string attribute) => doc.Descendants()
            .Where(e => e.Name.LocalName == "Compile")
            .Select(e => (string?)e.Attribute(attribute))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(v => !v.Contains('*', StringComparison.Ordinal) && !v.Contains("$(", StringComparison.Ordinal))
            .Select(v => Path.GetFullPath(Path.Combine(projectDir, v.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

        var removed = new HashSet<string>(ResolveItems("Remove"), StringComparer.OrdinalIgnoreCase);
        var nestedProjectDirs = Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(p))! + Path.DirectorySeparatorChar)
            .Where(d => !string.Equals(d, projectDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var defaultItemsDisabled = doc.Descendants()
            .Any(e => e.Name.LocalName is "EnableDefaultCompileItems" or "EnableDefaultItems" &&
                      string.Equals(e.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));

        var globbed = defaultItemsDisabled
            ? Enumerable.Empty<string>()
            : Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories);

        var files = globbed
            .Select(Path.GetFullPath)
            .Where(f => !nestedProjectDirs.Any(d => f.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            .Concat(ResolveItems("Include").Where(File.Exists))
            .Where(f => !removed.Contains(f))
            .ToList();

        log?.Invoke($"[NetAI] Found {files.Count} source file(s) in project '{Path.GetFileName(fullProject)}'.");
        return files;
    }

    private static bool MatchesAny(string file, IReadOnlyList<string> globs, bool emptyMeansAll)
    {
        if (globs.Count == 0) return emptyMeansAll;

        var name = Path.GetFileName(file);
        return globs.Any(glob => FileSystemName.MatchesSimpleExpression(glob, name, ignoreCase: true));
    }
}
