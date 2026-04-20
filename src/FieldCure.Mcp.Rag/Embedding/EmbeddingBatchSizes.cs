namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Known-good initial batch sizes for common provider/model combinations.
/// Values chosen conservatively to succeed on the first call without
/// triggering binary-split fallback. Unknown combinations fall through
/// to <see cref="DefaultBatchSize"/>.
/// </summary>
internal static class EmbeddingBatchSizes
{
    public const int DefaultBatchSize = 256;

    private static readonly Dictionary<string, int> KnownSizes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // OpenAI — generous API limits
            ["openai:text-embedding-3-small"] = 512,
            ["openai:text-embedding-3-large"] = 512,
            ["openai:text-embedding-ada-002"] = 512,

            // Ollama — local inference, GPU memory bound
            ["ollama:qwen3-embedding:8b"] = 64,
            ["ollama:qwen3-embedding:0.6b"] = 256,
            ["ollama:nomic-embed-text"] = 256,
            ["ollama:mxbai-embed-large"] = 128,
        };

    /// <summary>
    /// Returns the recommended initial batch size for the given provider and model.
    /// </summary>
    public static int Resolve(string provider, string model)
    {
        var key = $"{provider}:{model}";
        return KnownSizes.TryGetValue(key, out var size) ? size : DefaultBatchSize;
    }
}
