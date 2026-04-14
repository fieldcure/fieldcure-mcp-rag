using FieldCure.Mcp.Rag.Models;

namespace FieldCure.Mcp.Rag.Storage;

/// <summary>
/// Per-chunk status information written alongside the chunk row.
/// </summary>
public sealed record ChunkWriteInfo
{
    /// <summary>Enriched (or original) text to index for search.</summary>
    public required string EnrichedText { get; init; }

    /// <summary>Chunk indexing state.</summary>
    public required ChunkIndexStatus Status { get; init; }

    /// <summary>Whether AI contextualization was successfully applied.</summary>
    public required bool IsContextualized { get; init; }

    /// <summary>Error message if the chunk failed at some stage. Null on success.</summary>
    public string? LastError { get; init; }
}
