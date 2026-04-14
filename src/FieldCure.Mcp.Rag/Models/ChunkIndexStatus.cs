namespace FieldCure.Mcp.Rag.Models;

/// <summary>
/// Tracks the indexing state of an individual chunk.
/// Stored in the <c>chunks.status</c> column as an integer.
/// </summary>
public enum ChunkIndexStatus
{
    /// <summary>Fully indexed: extracted, contextualized (if configured), and embedded.</summary>
    Indexed = 0,

    /// <summary>Indexed without contextualization (original text used as enriched).</summary>
    IndexedRaw = 1,

    /// <summary>Text extracted and chunked but embedding not yet generated.</summary>
    PendingEmbedding = 2,

    /// <summary>File recognized but text extraction not yet attempted.</summary>
    PendingExtraction = 3,

    /// <summary>Processing failed at some stage; see <c>last_error</c> for details.</summary>
    Failed = 4,
}
