namespace FieldCure.Mcp.Rag.Storage;

/// <summary>
/// A chunk in <see cref="Models.ChunkIndexStatus.PendingEmbedding"/> state
/// eligible for deferred embedding retry.
/// </summary>
public sealed record PendingChunk(
    string Id,
    string SourcePath,
    int ChunkIndex,
    string Content,
    string EnrichedText,
    int RetryCount);
