namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Fallback embedding provider that returns zero vectors.
/// Used when no embedding service is configured.
/// </summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public int Dimension => 0;
    public string ModelId => "null";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return Task.FromResult(Array.Empty<float>());
    }

    public Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            results[i] = Array.Empty<float>();
        return Task.FromResult(results);
    }
}
