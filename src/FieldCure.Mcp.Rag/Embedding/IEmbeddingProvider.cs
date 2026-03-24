namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Abstraction for generating text embedding vectors.
/// Supports OpenAI-compatible APIs (OpenAI, Azure OpenAI, Ollama, LM Studio)
/// and local ONNX models.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Gets the vector dimension produced by this provider.</summary>
    int Dimension { get; }

    /// <summary>Gets the model identifier string (used for DB storage).</summary>
    string ModelId { get; }

    /// <summary>
    /// Generates an embedding vector for a single text input.
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Generates embeddings for multiple texts.
    /// Default implementation calls EmbedAsync sequentially.
    /// Override for providers that support native batching.
    /// </summary>
    virtual async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
            results[i] = await EmbedAsync(texts[i], ct);
        return results;
    }
}
