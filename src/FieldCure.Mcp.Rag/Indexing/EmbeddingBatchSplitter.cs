using System.Net;
using FieldCure.Mcp.Rag.Embedding;
using Microsoft.Extensions.Logging;

namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Per-chunk failure isolation via binary split on <c>EmbedBatchAsync</c>
/// rejections. Used by both the main indexing loop and the deferred retry
/// second pass so that a single bad chunk (e.g. one that exceeds the
/// embedding model's per-input token limit) cannot hold the rest of a file
/// hostage. Stateless by design — all dependencies are passed in, so unit
/// tests can exercise it with a mocked <see cref="IEmbeddingProvider"/> and
/// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/>.
///
/// Logging contract:
/// <list type="bullet">
///   <item><description>Happy path (provider accepts first try) is silent.</description></item>
///   <item><description>On the first rejection the helper emits an Info-level
///     <c>start</c> line and then traces each sub-batch attempt at Debug level
///     (<c>attempt</c> → <c>OK</c> / <c>FAILED</c>). Per-chunk terminal
///     failures (size=1) and safety guard triggers are Warning-level. The
///     trace closes with an Info-level <c>done</c> line carrying promoted/
///     failed counts plus split diagnostics (<c>depthMax</c>, <c>providerCalls</c>).</description></item>
/// </list>
/// </summary>
internal static class EmbeddingBatchSplitter
{
    /// <summary>Maximum recursion depth. Beyond this, the helper falls back to deferred.</summary>
    public const int MaxSplitDepth = 20;

    /// <summary>
    /// Attempts to embed <paramref name="enrichedTexts"/> in a single batch.
    /// If the provider rejects the batch, recursively halves until individual
    /// chunks are isolated (size-1 base case → mark as Failed) or a safety
    /// guard triggers a deferred fallback.
    ///
    /// Happy path: one provider call, no recursion, returns all embeddings.
    ///
    /// Partial-failure path (e.g. one chunk over the token limit in a 1250-chunk
    /// batch): ~log₂(N) + k calls in the worst case, where k is the number of
    /// rejected chunks. Each rejected chunk converges to a size-1 base case.
    ///
    /// Safety guards:
    /// <list type="bullet">
    ///   <item><description><see cref="MaxSplitDepth"/> caps the recursion.</description></item>
    ///   <item><description>At depth 0, &gt;50% failure ratio is treated as a
    ///     provider-wide issue and triggers a deferred fallback so the next
    ///     exec gets a clean retry attempt.</description></item>
    /// </list>
    ///
    /// The helper never touches the store — the caller owns promotion and
    /// failed-chunk marking, so the same function works for the main loop
    /// (Commit 2a promotion path) and the second pass
    /// (<c>PromoteChunksToIndexedAsync</c> path).
    /// </summary>
    /// <param name="sourceLabel">
    /// Optional human-readable identifier (typically the storage path of the
    /// source file) included in <c>start</c> and <c>done</c> log lines so the
    /// trace can be correlated with a specific file. Omitted when null.
    /// </param>
    public static async Task<EmbedWithSplitResult> EmbedWithBinarySplitAsync(
        IEmbeddingProvider provider,
        ILogger logger,
        IReadOnlyList<string> chunkIds,
        IReadOnlyList<string> enrichedTexts,
        CancellationToken ct,
        string? sourceLabel = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(chunkIds);
        ArgumentNullException.ThrowIfNull(enrichedTexts);

        if (chunkIds.Count != enrichedTexts.Count)
            throw new ArgumentException(
                $"chunkIds.Count ({chunkIds.Count}) must equal enrichedTexts.Count ({enrichedTexts.Count}).");

        if (chunkIds.Count == 0)
            return new EmbedWithSplitResult([], [], DeferredFallback: false);

        var diag = new BinarySplitDiagnostics();
        var result = await EmbedCoreAsync(
            provider, logger, chunkIds, enrichedTexts,
            absoluteOffset: 0, depth: 0, diag, sourceLabel, ct);

        // Only close the trace when we actually entered split mode — the
        // happy path must stay silent so normal runs don't spam logs.
        if (diag.HasEnteredSplitMode)
        {
            var label = FormatLabel(sourceLabel);
            logger.LogInformation(
                "[BinarySplit] done {Label}promoted={Promoted} failed={Failed} " +
                "fallback={Fallback} depthMax={DepthMax} providerCalls={ProviderCalls}",
                label, result.Succeeded.Count, result.FailedChunkIds.Count,
                result.DeferredFallback, diag.MaxDepthReached, diag.ProviderCalls);
        }

        return result;
    }

    static async Task<EmbedWithSplitResult> EmbedCoreAsync(
        IEmbeddingProvider provider,
        ILogger logger,
        IReadOnlyList<string> chunkIds,
        IReadOnlyList<string> enrichedTexts,
        int absoluteOffset,
        int depth,
        BinarySplitDiagnostics diag,
        string? sourceLabel,
        CancellationToken ct)
    {
        diag.RecordDepth(depth);

        var rangeStart = absoluteOffset;
        var rangeEnd = absoluteOffset + chunkIds.Count - 1;

        // Depth guard: stop recursing into pathological splits.
        if (depth > MaxSplitDepth)
        {
            logger.LogWarning(
                "[BinarySplit] depth={Depth} range=[{Start}..{End}] exceeded " +
                "MaxSplitDepth={Max} — falling back to deferred",
                depth, rangeStart, rangeEnd, MaxSplitDepth);
            return new EmbedWithSplitResult([], [], DeferredFallback: true);
        }

        // Once we're in split mode (first failure already logged "start"),
        // every sub-batch attempt is traced so operators can reconstruct the
        // tree. The top-level attempt is deliberately silent so happy path
        // runs stay noise-free.
        if (diag.HasEnteredSplitMode)
        {
            logger.LogDebug(
                "[BinarySplit] depth={Depth} range=[{Start}..{End}] size={Size} attempt",
                depth, rangeStart, rangeEnd, chunkIds.Count);
        }

        float[][] embeddings;
        diag.ProviderCalls++;
        try
        {
            embeddings = await provider.EmbedBatchAsync(enrichedTexts, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException httpEx)
            when (httpEx.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Auth/authorization failure is provider-wide and non-recoverable
            // inside the split. Bubble it up so the caller can flag
            // ProviderHealth and bail out instead of thrashing on retries.
            throw;
        }
        catch (Exception ex)
        {
            // First rejection at depth 0 flips the diagnostic into split mode
            // and emits the "start" lifecycle line. Subsequent rejections at
            // deeper levels only add to the running trace.
            if (!diag.HasEnteredSplitMode)
            {
                diag.HasEnteredSplitMode = true;
                logger.LogInformation(
                    "[BinarySplit] start {Label}chunks={Count} — initial batch rejected",
                    FormatLabel(sourceLabel), chunkIds.Count);
            }

            // Base case: a single chunk the provider refuses cannot be split
            // further. Caller will mark it Failed. Log at Warning because this
            // is actionable — the caller sees which chunk_id to investigate.
            if (chunkIds.Count == 1)
            {
                logger.LogWarning(ex,
                    "[BinarySplit] depth={Depth} range=[{Start}..{End}] size=1 FAILED — " +
                    "chunk {ChunkId} marked Failed: {Error}",
                    depth, rangeStart, rangeEnd, chunkIds[0], ex.Message);
                return new EmbedWithSplitResult([], new[] { chunkIds[0] }, DeferredFallback: false);
            }

            // Non-terminal failure: log the rejection at Debug (we're still
            // making progress via the split) and recurse into both halves.
            logger.LogDebug(
                "[BinarySplit] depth={Depth} range=[{Start}..{End}] FAILED — {Error}",
                depth, rangeStart, rangeEnd, ex.Message);

            var mid = chunkIds.Count / 2;
            var leftIds = Slice(chunkIds, 0, mid);
            var leftTexts = Slice(enrichedTexts, 0, mid);
            var rightIds = Slice(chunkIds, mid, chunkIds.Count - mid);
            var rightTexts = Slice(enrichedTexts, mid, chunkIds.Count - mid);

            var left = await EmbedCoreAsync(
                provider, logger, leftIds, leftTexts,
                absoluteOffset, depth + 1, diag, sourceLabel, ct);
            if (left.DeferredFallback)
                return new EmbedWithSplitResult([], [], DeferredFallback: true);

            var right = await EmbedCoreAsync(
                provider, logger, rightIds, rightTexts,
                absoluteOffset + mid, depth + 1, diag, sourceLabel, ct);
            if (right.DeferredFallback)
                return new EmbedWithSplitResult([], [], DeferredFallback: true);

            var combinedSucceeded = new List<(string ChunkId, float[] Embedding)>(
                left.Succeeded.Count + right.Succeeded.Count);
            combinedSucceeded.AddRange(left.Succeeded);
            combinedSucceeded.AddRange(right.Succeeded);

            var combinedFailed = new List<string>(
                left.FailedChunkIds.Count + right.FailedChunkIds.Count);
            combinedFailed.AddRange(left.FailedChunkIds);
            combinedFailed.AddRange(right.FailedChunkIds);

            // Ratio guard — only at the top call. Sub-calls see partial views
            // whose local ratio is meaningless. A top-level >50% failure
            // suggests the provider itself is broken (auth, quota, model down),
            // not legitimate per-chunk rejections — roll back to deferred so
            // the next exec can retry with a clean slate.
            if (depth == 0 && combinedFailed.Count * 2 > chunkIds.Count)
            {
                logger.LogWarning(
                    "[BinarySplit] depth=0 failure ratio exceeded 50% " +
                    "({Failed}/{Total}) — treating as provider failure, falling back to deferred",
                    combinedFailed.Count, chunkIds.Count);
                return new EmbedWithSplitResult([], [], DeferredFallback: true);
            }

            return new EmbedWithSplitResult(combinedSucceeded, combinedFailed, DeferredFallback: false);
        }

        // Success at this level — only trace once we're in split mode, so
        // the happy path remains silent.
        if (diag.HasEnteredSplitMode)
        {
            logger.LogDebug(
                "[BinarySplit] depth={Depth} range=[{Start}..{End}] OK (promoted={Size})",
                depth, rangeStart, rangeEnd, chunkIds.Count);
        }

        var succeeded = new (string ChunkId, float[] Embedding)[chunkIds.Count];
        for (var i = 0; i < chunkIds.Count; i++)
            succeeded[i] = (chunkIds[i], embeddings[i]);
        return new EmbedWithSplitResult(succeeded, [], DeferredFallback: false);
    }

    static string FormatLabel(string? sourceLabel)
        => sourceLabel is null ? "" : $"sourcePath=\"{sourceLabel}\" ";

    /// <summary>Returns an array slice without the allocation of LINQ + ToList.</summary>
    static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> source, int start, int count)
    {
        var result = new T[count];
        for (var i = 0; i < count; i++)
            result[i] = source[start + i];
        return result;
    }

    /// <summary>
    /// Mutable, per-call diagnostic counters. Carried through recursion so
    /// the top-level <c>done</c> line can report the full tree shape
    /// (provider call count, deepest split reached) without every level
    /// having to thread tuples up manually.
    /// </summary>
    sealed class BinarySplitDiagnostics
    {
        /// <summary>
        /// Number of <c>EmbedBatchAsync</c> round-trips actually issued.
        /// Direct cost signal — each increment is one network/provider call,
        /// regardless of whether the sub-batch succeeded or was rejected.
        /// </summary>
        public int ProviderCalls { get; set; }

        public int MaxDepthReached { get; set; }

        /// <summary>
        /// <c>true</c> once the first rejection fired at any level. Gates
        /// both the lifecycle <c>start</c>/<c>done</c> lines and the
        /// per-depth trace so happy-path calls stay silent.
        /// </summary>
        public bool HasEnteredSplitMode { get; set; }

        public void RecordDepth(int depth)
        {
            if (depth > MaxDepthReached) MaxDepthReached = depth;
        }
    }
}
