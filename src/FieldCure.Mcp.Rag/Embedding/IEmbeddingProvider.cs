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
    /// Used at indexing time (document side of the retrieval pair).
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Generates a query-time embedding vector. Providers with asymmetric
    /// retrieval support (e.g., Gemini's <c>task_type</c>) should override
    /// this to emit a query-optimized vector. Default implementation
    /// delegates to <see cref="EmbedAsync"/> for symmetric embedders
    /// (Ollama, OpenAI text-embedding-3, etc.).
    /// </summary>
    /// <remarks>
    /// IMPORTANT: Vector store writes MUST go through <see cref="EmbedAsync"/>
    /// and search-time queries MUST go through this method. Mixing the two on
    /// asymmetric embedders silently degrades recall — the failure mode is
    /// reduced ranking quality, not an exception.
    /// </remarks>
    virtual Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default)
        => EmbedAsync(query, ct);

    /// <summary>
    /// Generates embeddings for multiple texts.
    /// Default implementation calls EmbedAsync sequentially.
    /// Override for providers that support native batching.
    /// </summary>
    virtual async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            results[i] = await EmbedAsync(texts[i], ct);
        return results;
    }
}
