using FieldCure.Mcp.Rag.Models;

namespace FieldCure.Mcp.Rag.Storage;

/// <summary>
/// File-level aggregated status written to file_index alongside the chunks.
/// Computed by the caller from per-chunk results.
/// </summary>
public sealed record FileWriteInfo
{
    /// <summary>Overall file indexing state.</summary>
    public required FileIndexStatus Status { get; init; }

    /// <summary>File content hash for change detection.</summary>
    public required string FileHash { get; init; }

    /// <summary>Number of chunks indexed without contextualization.</summary>
    public required int ChunksRaw { get; init; }

    /// <summary>Number of chunks successfully contextualized.</summary>
    public int ChunksContextualized { get; init; }

    /// <summary>Number of chunks pending embedding.</summary>
    public required int ChunksPending { get; init; }

    /// <summary>Error message for the most recent failure. Null on success.</summary>
    public string? LastError { get; init; }

    /// <summary>Pipeline stage where the error occurred (e.g., "parse", "embed"). Null on success.</summary>
    public string? LastErrorStage { get; init; }
}
