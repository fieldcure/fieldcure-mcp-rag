namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Thrown when the embedding provider fails (network, rate limit, provider down).
/// Wraps HttpRequestException etc. to make stage-4 failures distinguishable.
/// </summary>
public sealed class EmbeddingException : Exception
{
    public string FilePath { get; }

    public EmbeddingException(string filePath, string message, Exception? inner = null)
        : base(message, inner)
    {
        FilePath = filePath;
    }
}
