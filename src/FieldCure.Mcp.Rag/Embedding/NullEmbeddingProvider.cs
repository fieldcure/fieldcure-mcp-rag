namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Fallback embedding provider that returns zero vectors.
/// Used when no embedding service is configured.
/// </summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    /// <inheritdoc />
    public int Dimension => 0;
    /// <inheritdoc />
    public string ModelId => "null";

    /// <inheritdoc />
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return Task.FromResult(Array.Empty<float>());
    }

    /// <inheritdoc />
    public Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            results[i] = Array.Empty<float>();
        return Task.FromResult(results);
    }
}
