namespace FieldCure.Mcp.Rag.Models;

/// <summary>
/// Health status of upstream AI providers used during indexing.
/// Written to <c>_indexing_lock.provider_health</c> for real-time monitoring.
/// </summary>
public enum ProviderHealth
{
    /// <summary>All providers responding normally.</summary>
    Ok = 0,

    /// <summary>Embedding provider failed (network, rate limit, or provider down).</summary>
    EmbeddingUnavailable = 1,

    /// <summary>Contextualization provider failed (LLM API errors).</summary>
    ContextualizerUnavailable = 2,
}
