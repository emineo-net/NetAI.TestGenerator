using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;       // <--- Neu für JsonConvert
using Newtonsoft.Json.Linq;  // <--- Neu für JObject

namespace NetAI.TestGenerator.Core
{
    public class LocalLlmClient
    {
        private static readonly HttpClient SharedHttp = new()
        {
            BaseAddress = new Uri("http://localhost:8080/"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        public LocalLlmClient() { }

        public async Task<string> AskAsync(string userMessage, string? systemMessage = null, CancellationToken ct = default)
        {
            var messages = new List<object>();
            if (systemMessage is not null)
            {
                messages.Add(new { role = "system", content = systemMessage });
            }
            messages.Add(new { role = "user", content = userMessage });

            // 1. Request-Body mit Newtonsoft serialisieren
            var jsonPayload = JsonConvert.SerializeObject(new { messages, stream = false });
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            // 2. POST-Request senden
            using var response = await SharedHttp.PostAsync("v1/chat/completions", content, ct);
            response.EnsureSuccessStatusCode();

            // 3. Response-Body als String lesen
#if NET6_0_OR_GREATER
            var responseString = await response.Content.ReadAsStringAsync(ct);
#else
            var responseString = await response.Content.ReadAsStringAsync();
#endif

            // 4. Mit JObject (entspricht JsonElement) parsen und auslesen
            var json = JObject.Parse(responseString);

            var result = json["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
            return result;
        }
    }
}
