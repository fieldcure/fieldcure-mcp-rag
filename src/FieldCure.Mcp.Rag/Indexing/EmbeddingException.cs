using System.Net;

namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Thrown when the embedding provider fails (network, rate limit, provider down,
/// API rejection). Wraps the underlying transport exception to make stage-4
/// failures distinguishable from other indexing errors.
/// </summary>
public sealed class EmbeddingException : Exception
{
    /// <summary>Path of the file whose embedding batch failed.</summary>
    public string FilePath { get; }

    /// <summary>
    /// HTTP status code from the embedding API, when the failure originated from a
    /// non-success response. Null for transport-level failures (timeout, DNS,
    /// connection refused) where no HTTP response was received.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    public EmbeddingException(
        string filePath,
        string message,
        Exception? inner = null,
        HttpStatusCode? statusCode = null)
        : base(message, inner)
    {
        FilePath = filePath;
        StatusCode = statusCode;
    }
}
