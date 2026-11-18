using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Infrastructure;

namespace NetAI.TestGenerator.Cli.Infrastructure;

public static class ProjectLocator
{
    /// <summary>
    /// Finds the existing test project for a source file (same rules as the MSBuild task, see <see cref="TestProjectLocator"/>).
    /// </summary>
    public static string? ResolveTestProjectDirectory(string sourceFilePath, AiTestingConfig? config = null)
    {
        var csproj = FindUpwards(Path.GetDirectoryName(Path.GetFullPath(sourceFilePath)), "*.csproj");
        if (csproj is null) return null;

        var projectName = Path.GetFileNameWithoutExtension(csproj);
        return TestProjectLocator.FindExisting(Path.GetDirectoryName(csproj)!, TestProjectLocator.GetTestProjectName(projectName, config));
    }

    public static string? FindUpwards(string? startDirectory, string pattern)
    {
        for (var dir = startDirectory is null ? null : new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var hit = Directory.EnumerateFiles(dir.FullName, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (hit is not null) return hit;
        }

        return null;
    }
}
