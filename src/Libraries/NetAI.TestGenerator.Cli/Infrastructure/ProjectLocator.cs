namespace NetAI.TestGenerator.Cli.Infrastructure;

public static class ProjectLocator
{
    public static string? ResolveTestProjectDirectory(string sourceFilePath)
    {
        var srcDir = Path.GetDirectoryName(sourceFilePath);
        var csproj = FindUpwards(srcDir, "*.csproj");
        if (csproj is null) return null;

        var projectDir = Path.GetDirectoryName(csproj)!;
        var projectName = Path.GetFileNameWithoutExtension(csproj);

        // Kandidat 1: Geschwisterordner "<Project>.Tests"
        var parent = Path.GetDirectoryName(projectDir);
        if (parent is not null)
        {
            var sibling = Path.Combine(parent, $"{projectName}.Tests");
            if (Directory.Exists(sibling)) return sibling;

            // Kandidat 2: Parent/tests/<Project>.Tests
            var testsSibling = Path.Combine(parent, "tests", $"{projectName}.Tests");
            if (Directory.Exists(testsSibling)) return testsSibling;
        }

        return null;
    }

    public static string? FindUpwards(string? startDirectory, string pattern)
    {
        var dir = startDirectory is null ? null : new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var hit = Directory.EnumerateFiles(dir.FullName, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (hit is not null) return hit;
            dir = dir.Parent;
        }
        return null;
    }
}