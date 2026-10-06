using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetAI.TestGenerator.Core;

/// <summary>Sends chat-completion requests to a local OpenAI-compatible model endpoint.</summary>
public class LocalLlmClient
{
    private static readonly HttpClient SharedHttp = new()
    {
        BaseAddress = new Uri("http://localhost:8080/"), Timeout = TimeSpan.FromMinutes(5)
    };

    /// <summary>Creates a client for the configured local model endpoint.</summary>
    public LocalLlmClient()
    {
    }

    /// <summary>Submits a user message and returns the model's response text.</summary>
    public async Task<string> AskAsync(string userMessage, string? systemMessage = null, CancellationToken ct = default)
    {
        var messages = new List<object>();
        if (systemMessage is not null)
        {
            messages.Add(new { role = "system", content = systemMessage });
        }

        messages.Add(new { role = "user", content = userMessage });

        var jsonPayload = JsonConvert.SerializeObject(new { messages, stream = false });
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var response = await SharedHttp.PostAsync("v1/chat/completions", content, ct);
        response.EnsureSuccessStatusCode();

#if NET6_0_OR_GREATER
            var responseString = await response.Content.ReadAsStringAsync(ct);
#else
        var responseString = await response.Content.ReadAsStringAsync();
#endif

        var json = JObject.Parse(responseString);

        var result = json["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
        return result;
    }
}