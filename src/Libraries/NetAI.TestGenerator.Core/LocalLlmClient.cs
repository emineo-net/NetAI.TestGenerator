using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetAI.TestGenerator.Core
{
    /// <summary>Sends chat-completion requests to a local OpenAI-compatible model endpoint.</summary>
    /// <remarks>The endpoint is currently fixed at <c>http://localhost:8080/v1/chat/completions</c>.</remarks>
    public class LocalLlmClient
    {
        private static readonly HttpClient SharedHttp = new()
        {
            BaseAddress = new Uri("http://localhost:8080/"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        /// <summary>Creates a client for the configured local model endpoint.</summary>
        public LocalLlmClient() { }

        /// <summary>Submits a user message and returns the model's response text.</summary>
        /// <param name="userMessage">The message to send as the user role.</param>
        /// <param name="systemMessage">Optional system instructions for the model.</param>
        /// <param name="ct">Token used to cancel the HTTP request.</param>
        /// <returns>The content of the first chat-completion choice.</returns>
        /// <exception cref="HttpRequestException">The endpoint returns an unsuccessful status code.</exception>
        /// <exception cref="TaskCanceledException">The request times out or is canceled.</exception>
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
}
