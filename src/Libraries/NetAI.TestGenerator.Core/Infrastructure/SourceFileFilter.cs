namespace NetAI.TestGenerator.Core.Infrastructure;

/// <summary>Decides which C# files are candidates for test generation. Shared by the CLI and the MSBuild task.</summary>
public static class SourceFileFilter
{
    private static readonly string[] ExcludedDirectories = { "obj", "bin", ".git", ".vs", ".idea", "node_modules" };

    private static readonly string[] ExcludedFileSuffixes =
    {
        ".Designer.cs", ".g.cs", ".g.i.cs", ".AssemblyAttributes.cs", "AssemblyInfo.cs", "GlobalUsings.cs"
    };

    /// <summary>
    /// Returns <c>true</c> if <paramref name="filePath"/> is a hand-written C# source file that should get tests.
    /// Directory exclusions (obj, bin, ...) are evaluated relative to <paramref name="baseDirectory"/> when given, so a
    /// repository that itself lives below a folder named "bin" is not excluded entirely.
    /// </summary>
    public static bool IsCandidate(string filePath, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var name = Path.GetFileName(filePath);
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            ExcludedFileSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) ||
            // Case-sensitive on purpose: "FooTests.cs" is a test file, "Contests.cs" is not.
            Path.GetFileNameWithoutExtension(name).EndsWith("Tests", StringComparison.Ordinal))
        {
            return false;
        }

        var relative = GetRelativePath(baseDirectory, filePath);
        var directories = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The last segment is the file name itself.
        for (var i = 0; i < directories.Length - 1; i++)
        {
            var segment = directories[i];
            if (ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase) ||
                segment.IndexOf("_wpftmp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetRelativePath(string? baseDirectory, string filePath)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return filePath;
        }

        var fullBase = Path.GetFullPath(baseDirectory!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        var fullFile = Path.GetFullPath(filePath);

        return fullFile.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase) ? fullFile.Substring(fullBase.Length) : fullFile;
    }
}
