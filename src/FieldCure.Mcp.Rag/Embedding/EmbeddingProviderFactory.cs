using FieldCure.Mcp.Rag.Configuration;

namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Central factory for <see cref="IEmbeddingProvider"/> construction.
/// Unifies the switch-on-string logic previously duplicated across
/// <c>ExecQueueRunner</c>, <c>Program</c>, and <c>SearchDocumentsTool</c>.
/// </summary>
/// <remarks>
/// Callers are responsible for resolving the API key (env var, credential
/// vault, or MCP elicitation). This factory only consumes the resolved key.
/// Pass an empty string for local providers that do not need a key (Ollama).
/// </remarks>
internal static class EmbeddingProviderFactory
{
    /// <summary>
    /// Creates an embedding provider from configuration.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <param name="apiKey">Resolved API key; empty for local providers (Ollama).</param>
    /// <returns>Configured provider instance.</returns>
    /// <exception cref="NotSupportedException">Unknown provider kind.</exception>
    public static IEmbeddingProvider Create(ProviderConfig config, string apiKey)
    {
        var baseUrl = config.BaseUrl ?? DefaultBaseUrl(config.Provider);

        return config.Provider.ToLowerInvariant() switch
        {
            "ollama" => new OllamaEmbeddingProvider(
                baseUrl:   baseUrl,
                model:     config.Model,
                keepAlive: config.KeepAlive ?? OllamaDefaults.KeepAlive,
                dimension: config.Dimension),

            "openai" => new OpenAiCompatibleEmbeddingProvider(
                baseUrl:   baseUrl,
                apiKey:    apiKey,
                model:     config.Model,
                dimension: config.Dimension),

            "gemini" => new GeminiEmbeddingProvider(
                apiKey:    apiKey,
                model:     config.Model,
                dimension: config.Dimension), // 0 = API default (3072)

            _ => throw new NotSupportedException(
                $"Unknown embedding provider kind: '{config.Provider}'."),
        };
    }

    /// <summary>
    /// Returns the default base URL for known provider kinds. Falls back to
    /// the Ollama localhost endpoint to preserve historical behavior — three
    /// previous call sites all defaulted unknown providers to localhost:11434.
    /// </summary>
    public static string DefaultBaseUrl(string provider) =>
        provider.ToLowerInvariant() switch
        {
            "ollama" => "http://localhost:11434",
            "openai" => "https://api.openai.com",
            _ => "http://localhost:11434",
        };
}
