namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Creates an IEmbeddingProvider from environment variables.
///
/// Environment variables:
///   EMBEDDING_BASE_URL  - API base URL (default: "http://localhost:11434" for Ollama)
///   EMBEDDING_API_KEY   - API key (default: empty, for local servers)
///   EMBEDDING_MODEL     - Model name (default: "nomic-embed-text")
///   EMBEDDING_DIMENSION - Vector dimension (default: 0 = auto-detect)
/// </summary>
public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider CreateFromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable("EMBEDDING_BASE_URL")
                      ?? "http://localhost:11434";
        var apiKey = Environment.GetEnvironmentVariable("EMBEDDING_API_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable("EMBEDDING_MODEL")
                    ?? "nomic-embed-text";
        var dimension = int.TryParse(
            Environment.GetEnvironmentVariable("EMBEDDING_DIMENSION"), out var d) ? d : 0;

        return new OpenAiCompatibleEmbeddingProvider(baseUrl, apiKey, model, dimension);
    }
}
