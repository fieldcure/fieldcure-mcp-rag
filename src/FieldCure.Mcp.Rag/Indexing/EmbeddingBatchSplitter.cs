using System.Net;
using FieldCure.Mcp.Rag.Embedding;
using Microsoft.Extensions.Logging;

namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Per-chunk failure isolation via binary split on <see cref="EmbedBatchAsync"/>
/// rejections. Used by both the main indexing loop and the deferred retry
/// second pass so that a single bad chunk (e.g. one that exceeds the
/// embedding model's per-input token limit) cannot hold the rest of a file
/// hostage. Stateless by design — all dependencies are passed in, so unit
/// tests can exercise it with a mocked <see cref="IEmbeddingProvider"/> and
/// <see cref="NullLogger"/>.
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
    ///   <item><description>At depth 0, &gt;50% failure ratio is treated as a provider-wide issue and triggers a deferred fallback so the next exec gets a clean retry attempt.</description></item>
    /// </list>
    ///
    /// The helper never touches the store — the caller owns promotion and
    /// failed-chunk marking, so the same function works for the main loop
    /// (Commit 2a promotion path) and the second pass
    /// (<c>PromoteChunksToIndexedAsync</c> path).
    /// </summary>
    public static async Task<EmbedWithSplitResult> EmbedWithBinarySplitAsync(
        IEmbeddingProvider provider,
        ILogger logger,
        IReadOnlyList<string> chunkIds,
        IReadOnlyList<string> enrichedTexts,
        CancellationToken ct,
        int depth = 0)
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

        // Depth guard: stop recursing into pathological splits.
        if (depth > MaxSplitDepth)
        {
            logger.LogWarning(
                "[BinarySplit] Exceeded MaxSplitDepth={Max} at {Count} chunks; falling back to deferred.",
                MaxSplitDepth, chunkIds.Count);
            return new EmbedWithSplitResult([], [], DeferredFallback: true);
        }

        float[][] embeddings;
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
            // Base case: a single chunk the provider refuses cannot be split
            // further. Caller will mark it Failed.
            if (chunkIds.Count == 1)
            {
                logger.LogWarning(ex,
                    "[BinarySplit] Chunk {Id} rejected by embedding provider: {Error}",
                    chunkIds[0], ex.Message);
                return new EmbedWithSplitResult([], new[] { chunkIds[0] }, DeferredFallback: false);
            }

            // Recursive case: split in half.
            var mid = chunkIds.Count / 2;
            var leftIds = Slice(chunkIds, 0, mid);
            var leftTexts = Slice(enrichedTexts, 0, mid);
            var rightIds = Slice(chunkIds, mid, chunkIds.Count - mid);
            var rightTexts = Slice(enrichedTexts, mid, chunkIds.Count - mid);

            var left = await EmbedWithBinarySplitAsync(
                provider, logger, leftIds, leftTexts, ct, depth + 1);
            if (left.DeferredFallback)
                return new EmbedWithSplitResult([], [], DeferredFallback: true);

            var right = await EmbedWithBinarySplitAsync(
                provider, logger, rightIds, rightTexts, ct, depth + 1);
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
            // whose local ratio is meaningless. A top-level &gt;50% failure
            // suggests the provider itself is broken (auth, quota, model down),
            // not legitimate per-chunk rejections — roll back to deferred so
            // the next exec can retry with a clean slate.
            if (depth == 0 && combinedFailed.Count * 2 > chunkIds.Count)
            {
                logger.LogWarning(
                    "[BinarySplit] Failure ratio exceeded 50% ({Failed}/{Total}); " +
                    "treating as provider failure and falling back to deferred.",
                    combinedFailed.Count, chunkIds.Count);
                return new EmbedWithSplitResult([], [], DeferredFallback: true);
            }

            return new EmbedWithSplitResult(combinedSucceeded, combinedFailed, DeferredFallback: false);
        }

        // Happy path: the provider accepted the whole batch.
        var succeeded = new (string ChunkId, float[] Embedding)[chunkIds.Count];
        for (var i = 0; i < chunkIds.Count; i++)
            succeeded[i] = (chunkIds[i], embeddings[i]);
        return new EmbedWithSplitResult(succeeded, [], DeferredFallback: false);
    }

    /// <summary>Returns an array slice without the allocation of LINQ + ToList.</summary>
    static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> source, int start, int count)
    {
        var result = new T[count];
        for (var i = 0; i < count; i++)
            result[i] = source[start + i];
        return result;
    }
}
