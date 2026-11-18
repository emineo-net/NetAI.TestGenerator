using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ↓ DIESE Zeile behebt den "nicht gefunden"-Fehler
using NetAI.TestGenerator.Core.Config;

namespace NetAI.TestGenerator.Core;

/// <summary>Sends chat-completion requests to an OpenAI-compatible model endpoint (local or hosted).</summary>
public class LocalLlmClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly LlmConnectionSettings _settings;
    private readonly bool _ownsHttpClient;
    private readonly string _chatCompletionsPath;

    /// <summary>Creates a client with default local settings (for backward compatibility).</summary>
    public LocalLlmClient()
        : this(new LlmConnectionSettings()) { }

    /// <summary>Creates a client for the given LLM connection settings.</summary>
    public LocalLlmClient(LlmConnectionSettings settings)
        : this(settings, null) { }

    /// <summary>Creates a client with an injectable <see cref="HttpClient"/> (e.g. for tests or DI).</summary>
    public LocalLlmClient(LlmConnectionSettings settings, HttpClient? httpClient)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.Validate();

        var baseUri = new Uri(_settings.BaseUrl, UriKind.Absolute);

        // Accept both "http://host:1234/" and "http://host:1234/v1/" – the latter would otherwise
        // resolve to ".../v1/v1/chat/completions" and fail with 404.
        _chatCompletionsPath = baseUri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? "chat/completions"
            : "v1/chat/completions";

        if (httpClient is null)
        {
            _http = new HttpClient
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromMinutes(Math.Max(1, _settings.TimeoutMinutes))
            };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
            _http.BaseAddress ??= baseUri;
            _ownsHttpClient = false;
        }

        var apiKey = _settings.ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>Gets the connection settings used by this client.</summary>
    public LlmConnectionSettings Settings => _settings;

    /// <summary>Submits a user message and returns the model's response text.</summary>
    public async Task<string> AskAsync(string userMessage, string? systemMessage = null, CancellationToken ct = default)
    {
        var messages = new List<object>();

        var sys = systemMessage ?? _settings.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(sys))
            messages.Add(new { role = "system", content = sys });

        messages.Add(new { role = "user", content = userMessage });

        var payload = new
        {
            model = _settings.Model,
            messages,
            temperature = _settings.Temperature,
            stream = false
        };

        var jsonPayload = JsonConvert.SerializeObject(payload);
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_chatCompletionsPath, content, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation; make it distinguishable from a user cancel.
            throw new TimeoutException($"The LLM request did not complete within {_http.Timeout.TotalMinutes:0.#} minute(s).", ex);
        }

        using (response)
        {
#if NET6_0_OR_GREATER
            var responseString = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
#else
            var responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif

            if (!response.IsSuccessStatusCode)
            {
                // Keep the server's error body (e.g. "context length exceeded"); EnsureSuccessStatusCode would drop it.
                var body = responseString.Length > 500 ? responseString.Substring(0, 500) + "..." : responseString;
                throw new HttpRequestException($"LLM endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            var choices = JObject.Parse(responseString)["choices"] as JArray;
            return choices is { Count: > 0 } ? choices[0]?["message"]?["content"]?.ToString() ?? "" : "";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}