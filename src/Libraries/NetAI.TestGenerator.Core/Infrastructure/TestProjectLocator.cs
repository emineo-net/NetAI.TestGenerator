using NetAI.TestGenerator.Core.Config;

namespace NetAI.TestGenerator.Core.Infrastructure;

/// <summary>
/// Finds the test project directory for a source project. Shared by the CLI and the MSBuild task so both
/// front-ends agree on where tests live.
/// </summary>
public static class TestProjectLocator
{
    /// <summary>Default location of newly created test projects, relative to the solution directory.</summary>
    public const string DefaultTestsRelativePath = "tests/UnitTests";

    private static readonly string[] TestFolderNames = { "tests", "test", Path.Combine("tests", "UnitTests"), Path.Combine("test", "UnitTests") };

    /// <summary>
    /// Returns the test project name from <c>environment.testProjectName</c> (supports the <c>{ProjectName}</c>
    /// placeholder), or <c>&lt;ProjectName&gt;.Tests</c>.
    /// </summary>
    public static string GetTestProjectName(string projectName, AiTestingConfig? config)
    {
        var configured = config?.Environment?.TestProjectName;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return $"{projectName}.Tests";
        }

        return configured!.Replace("{ProjectName}", projectName).Trim();
    }

    /// <summary>Finds an existing test project directory by walking up from the source project directory.</summary>
    public static string? FindExisting(string sourceProjectDirectory, string testProjectName)
    {
        for (var dir = new DirectoryInfo(sourceProjectDirectory); dir is not null; dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, testProjectName);
            if (IsProjectDirectory(direct))
            {
                return direct;
            }

            foreach (var folder in TestFolderNames)
            {
                var nested = Path.Combine(dir.FullName, folder, testProjectName);
                if (IsProjectDirectory(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the existing test project directory, or the default location below the solution directory
    /// (<c>&lt;solution&gt;/tests/UnitTests/&lt;TestProjectName&gt;</c>) where a new one will be created.
    /// Returns <c>null</c> if neither exists nor a solution directory can be found.
    /// </summary>
    public static string? Resolve(string sourceProjectDirectory, string projectName, AiTestingConfig? config)
    {
        var testProjectName = GetTestProjectName(projectName, config);
        var existing = FindExisting(sourceProjectDirectory, testProjectName);
        if (existing is not null)
        {
            return existing;
        }

        var solutionDirectory = FindSolutionDirectory(sourceProjectDirectory);
        return solutionDirectory is null
            ? null
            : Path.Combine(solutionDirectory, DefaultTestsRelativePath.Replace('/', Path.DirectorySeparatorChar), testProjectName);
    }

    /// <summary>Finds the nearest directory (walking up) that contains a <c>.sln</c> or <c>.slnx</c> file.</summary>
    public static string? FindSolutionDirectory(string startDirectory)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            try
            {
                if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
                {
                    return dir.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable directory: keep walking up.
            }
        }

        return null;
    }

    private static bool IsProjectDirectory(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any();
}
