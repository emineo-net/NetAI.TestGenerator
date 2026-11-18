using System.Xml.Linq;

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>Facts about a test project directory, read once per run.</summary>
public sealed class TestProjectInfo
{
    private TestProjectInfo(string directory, string? projectFile, XDocument? document, IReadOnlyList<string> packageIds,
        string? rootNamespace)
    {
        Directory = directory;
        ProjectFile = projectFile;
        Document = document;
        PackageIds = packageIds;
        RootNamespace = rootNamespace;
    }

    /// <summary>Gets the test project directory.</summary>
    public string Directory { get; }

    /// <summary>Gets the test project file, or <c>null</c> if the project does not exist yet.</summary>
    public string? ProjectFile { get; }

    /// <summary>Gets the parsed project file, or <c>null</c> if it does not exist or could not be parsed.</summary>
    public XDocument? Document { get; }

    /// <summary>Gets the IDs of all packages referenced by the test project.</summary>
    public IReadOnlyList<string> PackageIds { get; }

    /// <summary>Gets the explicit <c>RootNamespace</c> of the test project, if set.</summary>
    public string? RootNamespace { get; }

    /// <summary>Reads the test project in <paramref name="testProjectDirectory"/>; never throws.</summary>
    public static TestProjectInfo Load(string? testProjectDirectory)
    {
        var directory = testProjectDirectory ?? string.Empty;
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
        {
            return new TestProjectInfo(directory, null, null, Array.Empty<string>(), null);
        }

        var projectFile = System.IO.Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

        if (projectFile is null)
        {
            return new TestProjectInfo(directory, null, null, Array.Empty<string>(), null);
        }

        try
        {
            var doc = XDocument.Load(projectFile);
            var rootNamespace = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value?.Trim();

            return new TestProjectInfo(directory, projectFile, doc, ReadPackageIds(doc),
                string.IsNullOrWhiteSpace(rootNamespace) ? null : rootNamespace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new TestProjectInfo(directory, projectFile, null, Array.Empty<string>(), null);
        }
    }

    /// <summary>Reads package IDs from project files in the specified directory.</summary>
    public static IReadOnlyList<string> ReadPackageIds(string? testProjectDirectory) => Load(testProjectDirectory).PackageIds;

    private static IReadOnlyList<string> ReadPackageIds(XDocument doc)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var id = pkg.Attribute("Include")?.Value ?? pkg.Attribute("Update")?.Value;
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add(id!.Trim());
            }
        }

        return result.ToList();
    }
}
