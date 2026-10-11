namespace NetAI.TestGenerator.Cli.Infrastructure;

public static class ProjectLocator
{
    private static readonly string[] TestFolderNames = { "tests", "test", "Tests", "Test" };

    public static string? ResolveTestProjectDirectory(string sourceFilePath)
    {
        var csproj = FindUpwards(Path.GetDirectoryName(sourceFilePath), "*.csproj");
        if (csproj is null) return null;

        var projectDir = Path.GetDirectoryName(csproj)!;
        var projectName = Path.GetFileNameWithoutExtension(csproj);
        var candidateName = $"{projectName}.Tests";

        // Walk up from the source project directory toward the repo root.
        var dir = new DirectoryInfo(projectDir);
        while (dir is not null)
        {
            // 1) Direct sibling: <level>/<Project>.Tests
            var direct = Path.Combine(dir.FullName, candidateName);
            if (IsTestProjectDir(direct)) return direct;

            // 2) Nested: <level>/tests/<Project>.Tests (also test/Tests/Test)
            foreach (var sub in TestFolderNames)
            {
                var nested = Path.Combine(dir.FullName, sub, candidateName);
                if (IsTestProjectDir(nested)) return nested;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool IsTestProjectDir(string directory) =>
        Directory.Exists(directory) &&
        Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any();

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