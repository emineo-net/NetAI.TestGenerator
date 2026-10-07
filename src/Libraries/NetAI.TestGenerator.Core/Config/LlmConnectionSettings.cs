using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NetAI.TestGenerator.Core.Config;

/// <summary>
/// Defines the connection to an OpenAI-compatible LLM endpoint.
/// The same shape is used for local servers and hosted APIs – switching
/// endpoints only means changing <see cref="BaseUrl"/> and providing an
/// optional API key.
/// </summary>
public class LlmConnectionSettings
{
    /// <summary>Gets or sets the base URL of the endpoint. Must end with a trailing slash.</summary>
    [JsonProperty("baseUrl")]
    public string BaseUrl { get; set; } = "http://localhost:8080/";

    /// <summary>Gets or sets the model identifier.</summary>
    [JsonProperty("model")]
    public string Model { get; set; } = "gpt-4o";

    /// <summary>Gets or sets the sampling temperature (0.0 – 2.0).</summary>
    [JsonProperty("temperature")]
    public double Temperature { get; set; } = 0.2;

    /// <summary>Gets or sets the HTTP timeout in minutes.</summary>
    [JsonProperty("timeoutMinutes")]
    public int TimeoutMinutes { get; set; } = 5;

    /// <summary>Gets or sets the system instructions supplied to the model.</summary>
    [JsonProperty("systemPrompt")]
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Gets or sets an API key stored directly in the file.
    /// Mutually exclusive with <see cref="ApiKeyEnvVar"/>.
    /// </summary>
    [JsonProperty("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the name of an environment variable holding the API key.
    /// Mutually exclusive with <see cref="ApiKey"/>.
    /// </summary>
    [JsonProperty("apiKeyEnvVar")]
    public string? ApiKeyEnvVar { get; set; }

    /// <summary>Resolves the effective API key; the environment variable takes precedence.</summary>
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyEnvVar))
        {
            var fromEnv = Environment.GetEnvironmentVariable(ApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        }

        return string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey;
    }

    /// <summary>
    /// Validates the mutually exclusive fields and value ranges.
    /// Throws <see cref="InvalidOperationException"/> on any violation.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new InvalidOperationException("aiConfiguration.baseUrl must be set.");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"aiConfiguration.baseUrl is not a valid absolute URI: '{BaseUrl}'.");
        if (!BaseUrl.EndsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException("aiConfiguration.baseUrl must end with a trailing slash.");
        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("aiConfiguration.model must be set.");
        if (Temperature < 0 || Temperature > 2)
            throw new InvalidOperationException("aiConfiguration.temperature must be between 0 and 2.");
        if (TimeoutMinutes <= 0)
            throw new InvalidOperationException("aiConfiguration.timeoutMinutes must be greater than 0.");

        var hasInline = !string.IsNullOrWhiteSpace(ApiKey);
        var hasEnv = !string.IsNullOrWhiteSpace(ApiKeyEnvVar);
        if (hasInline && hasEnv)
            throw new InvalidOperationException(
                "aiConfiguration.apiKey and aiConfiguration.apiKeyEnvVar are mutually exclusive – set only one.");
    }
}