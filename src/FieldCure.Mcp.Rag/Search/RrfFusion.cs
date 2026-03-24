namespace FieldCure.Mcp.Rag.Search;

/// <summary>
/// Reciprocal Rank Fusion: merges multiple ranked lists into a single ranking.
/// Reference: Cormack, Clarke &amp; Buettcher (SIGIR 2009).
/// </summary>
internal static class RrfFusion
{
    /// <summary>
    /// Fuses multiple ranked ID lists using RRF scoring: score(d) = Σ 1/(k + rank_i).
    /// </summary>
    /// <param name="rankedLists">Each inner list is ordered by relevance (index 0 = most relevant).</param>
    /// <param name="topK">Maximum results to return.</param>
    /// <param name="k">RRF constant (default 60). Higher values dampen rank differences.</param>
    /// <returns>Fused (Id, Score) pairs sorted by descending RRF score.</returns>
    public static List<(string Id, double Score)> Fuse(
        IReadOnlyList<IReadOnlyList<string>> rankedLists, int topK, int k = 60)
    {
        if (rankedLists.Count == 0 || topK <= 0)
            return [];

        var scores = new Dictionary<string, double>();

        foreach (var list in rankedLists)
        {
            for (int rank = 0; rank < list.Count; rank++)
            {
                var id = list[rank];
                var rrfScore = 1.0 / (k + rank + 1);

                if (!scores.TryAdd(id, rrfScore))
                    scores[id] += rrfScore;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
