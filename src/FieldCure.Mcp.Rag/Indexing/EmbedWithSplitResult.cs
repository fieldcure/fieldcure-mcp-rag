namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Result of an embedding batch attempt that may have recursively split on
/// failure. Produced by <c>IndexingEngine.EmbedWithBinarySplitAsync</c> and
/// consumed at the Stage 4 call site (main loop) and the deferred retry
/// second pass.
/// </summary>
/// <param name="Succeeded">
/// Chunk id → embedding pairs for chunks whose embedding request was
/// accepted by the provider. The caller uses this to build the argument
/// list for <c>PromoteChunksToIndexedAsync</c>.
/// </param>
/// <param name="FailedChunkIds">
/// Chunk ids that failed as a size-1 batch — i.e. the provider rejected
/// that specific chunk, and splitting further is no longer possible.
/// The caller marks each of these as <see cref="Models.ChunkIndexStatus.Failed"/>
/// via <c>UpdateChunkStatusAsync</c>.
/// </param>
/// <param name="DeferredFallback">
/// <c>true</c> when a safety guard fired and the whole batch should be
/// left as <see cref="Models.ChunkIndexStatus.PendingEmbedding"/> for a
/// future exec to retry. Fires when either the split recursion hit
/// <c>MaxSplitDepth</c> or the top-level failure ratio exceeded 50%.
/// When this is <c>true</c>, <see cref="Succeeded"/> and
/// <see cref="FailedChunkIds"/> are both empty — the caller should
/// neither promote nor mark-as-failed any chunks and should treat the
/// whole file as deferred instead.
/// </param>
public sealed record EmbedWithSplitResult(
    IReadOnlyList<(string ChunkId, float[] Embedding)> Succeeded,
    IReadOnlyList<string> FailedChunkIds,
    bool DeferredFallback);
