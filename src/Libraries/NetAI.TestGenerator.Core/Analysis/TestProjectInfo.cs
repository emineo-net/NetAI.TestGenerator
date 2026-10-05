using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace NetAI.TestGenerator.Core.Analysis;

/// <summary>
///     Liest Paket-IDs aus dem Testprojekt (bzw. aus mehreren .csproj im Verzeichnis),
///     damit Profile und Prompt nur referenzieren, was tatsächlich installiert ist.
/// </summary>
public static class TestProjectInfo
{
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
                // .csproj defekt oder gesperrt – ignorieren, ist nur Best-Effort
            }
        }

        return result.ToList();
    }
}