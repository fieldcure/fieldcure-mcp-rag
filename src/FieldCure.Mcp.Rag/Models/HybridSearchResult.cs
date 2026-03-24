namespace FieldCure.Mcp.Rag.Models;

/// <summary>Result from HybridSearcher including the search mode that was used.</summary>
public record HybridSearchResult
{
    public required List<SearchResult> Results { get; init; }
    public required SearchMode Mode { get; init; }
    public required int TotalChunksSearched { get; init; }
}
