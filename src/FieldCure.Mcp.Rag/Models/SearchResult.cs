namespace FieldCure.Mcp.Rag.Models;

/// <summary>A single result from vector similarity search.</summary>
public record SearchResult
{
    public required string ChunkId { get; init; }
    public required string SourcePath { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }

    /// <summary>Cosine similarity or RRF score.</summary>
    public required float Score { get; init; }

    /// <summary>Total chunks from the same source document (for has_previous/has_next).</summary>
    public int TotalChunks { get; init; }
}
