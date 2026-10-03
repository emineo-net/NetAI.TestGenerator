using System.Text.RegularExpressions;

namespace WpftranlationTestApp;

public partial class MultilingualExtractor
{
    /// <summary>
    /// Extrahiert Entitäten aus deutschen, russischen oder anderen mehrsprachigen Texten.
    /// </summary>
    public async Task<int> ExtractEntitiesAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1;

        // 1. Semantische Extraktion via .NET 10 AI-Interface für komplexe Entitäten
        var prompt = """
            Extract the following entities from the text below: Prices, Addresses, Dates, and Headings.
            Focus heavily on German and Russian language nuances (e.g., Cyrillic addresses, ruble/euro formats).
            Return ONLY a valid JSON object matching this schema:
            {
              "prices": ["string"],
              "addresses": ["string"],
              "dates": ["string"],
              "headings": ["string"]
            }
            Text to analyze:
            """;

        var response = "some response";
      

        var result = "som result";


        return 2;
    }

    private static List<string> ExtractEmails(string text)
    {
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Deterministischer Scan mit schnellem Source-Generated Regex
        

        return [.. emails];
    }
}
