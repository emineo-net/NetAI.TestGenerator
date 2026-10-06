using System.Xml.Linq;

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>Reads package IDs from test project files in a directory.</summary>
public static class TestProjectInfo
{
    /// <summary>Reads package IDs from project files in the specified directory.</summary>
    public static IReadOnlyList<string> ReadPackageIds(string? testProjectDirectory)
    {
        if (string.IsNullOrWhiteSpace(testProjectDirectory) || !Directory.Exists(testProjectDirectory))
        {
            return Array.Empty<string>();
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var csproj in Directory.EnumerateFiles(testProjectDirectory, "*.csproj", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var doc = XDocument.Load(csproj);
                foreach (var pkg in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
                {
                    var id = pkg.Attribute("Include")?.Value ?? pkg.Attribute("Update")?.Value;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result.Add(id!.Trim());
                    }
                }
            }
            catch
            {
            }
        }

        return result.ToList();
    }
}