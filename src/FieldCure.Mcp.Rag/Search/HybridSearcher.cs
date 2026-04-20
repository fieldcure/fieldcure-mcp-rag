using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Storage;

namespace FieldCure.Mcp.Rag.Search;

/// <summary>
/// Orchestrates BM25 (FTS5) and vector searches, fusing results via RRF.
/// Graceful degradation: NullEmbeddingProvider → BM25 only, no FTS5 results → vector only.
/// </summary>
public sealed class HybridSearcher(SqliteVectorStore store, IEmbeddingProvider embeddingProvider)
{
    /// <summary>
    /// Searches the store using BM25, vector similarity, or hybrid fusion
    /// depending on provider availability and the requested mode.
    /// </summary>
    /// <param name="query">Natural-language search query.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="threshold">Minimum vector similarity score.</param>
    /// <param name="requestedMode">Optional explicit mode override.</param>
    /// <param name="ct">Cancellation token for the search operation.</param>
    /// <returns>A ranked hybrid search result.</returns>
    public async Task<HybridSearchResult> SearchAsync(
        string query, int topK, float threshold,
        SearchMode? requestedMode = null, CancellationToken ct = default)
    {
        var isVectorAvailable = embeddingProvider is not NullEmbeddingProvider;
        var candidateK = topK * 3;

        var skipVector = requestedMode == SearchMode.Bm25Only;
        var skipBm25 = requestedMode == SearchMode.VectorOnly;

        var bm25Results = skipBm25 ? [] : await store.SearchFtsAsync(query, candidateK);
        var isBm25Available = bm25Results.Count > 0;

        var mode = requestedMode switch
        {
            SearchMode.Bm25Only => SearchMode.Bm25Only,
            SearchMode.VectorOnly when isVectorAvailable => SearchMode.VectorOnly,
            SearchMode.VectorOnly => throw new InvalidOperationException(
                "Cannot use vector search: no embedding provider configured."),
            _ => (isVectorAvailable, isBm25Available) switch
            {
                (true, true) => SearchMode.Hybrid,
                (true, false) => SearchMode.VectorOnly,
                (false, true) => SearchMode.Bm25Only,
                (false, false) => SearchMode.Bm25Only,
            },
        };

        List<string> finalIds;
        List<(string Id, double Score)> scoredIds;

        if (mode == SearchMode.Hybrid)
        {
            // Vector search
            var queryEmbedding = await embeddingProvider.EmbedAsync(query, ct);
            var vectorResults = await store.SearchAsync(queryEmbedding, candidateK, threshold);

            var bm25Ids = bm25Results.Select(r => r.ChunkId).ToList();
            var vectorIds = vectorResults.Select(r => r.ChunkId).ToList();

            scoredIds = RrfFusion.Fuse([bm25Ids, vectorIds], topK);
            finalIds = scoredIds.Select(s => s.Id).ToList();
        }
        else if (mode == SearchMode.VectorOnly)
        {
            var queryEmbedding = await embeddingProvider.EmbedAsync(query, ct);
            var vectorResults = await store.SearchAsync(queryEmbedding, topK, threshold);

            scoredIds = vectorResults.Select(r => (r.ChunkId, (double)r.Score)).ToList();
            finalIds = scoredIds.Select(s => s.Id).ToList();
        }
        else // Bm25Only
        {
            scoredIds = bm25Results.Take(topK).ToList();
            finalIds = scoredIds.Select(s => s.Id).ToList();
        }

        // Hydrate chunks
        var chunks = await store.GetChunksByIdsAsync(finalIds);
        var chunkMap = chunks.ToDictionary(c => c.Id);

        // Build results preserving fusion order
        var scoreMap = scoredIds.ToDictionary(s => s.Id, s => s.Score);
        var results = new List<SearchResult>();
        foreach (var id in finalIds)
        {
            if (chunkMap.TryGetValue(id, out var chunk))
            {
                results.Add(new SearchResult
                {
                    ChunkId = chunk.Id,
                    SourcePath = chunk.SourcePath,
                    ChunkIndex = chunk.ChunkIndex,
                    Content = chunk.Content,
                    Score = (float)scoreMap.GetValueOrDefault(id),
                    TotalChunks = chunk.TotalChunks,
                });
            }
        }

        var totalChunks = await store.GetTotalChunkCountAsync();

        return new HybridSearchResult
        {
            Results = results,
            Mode = mode,
            TotalChunksSearched = totalChunks,
        };
    }
}
