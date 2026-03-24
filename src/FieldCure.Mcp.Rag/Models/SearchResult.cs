namespace FieldCure.Mcp.Rag.Models;

/// <summary>A single result from vector similarity search.</summary>
public record SearchResult
{
    public required string ChunkId { get; init; }
    public required string SourcePath { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }

    /// <summary>Cosine similarity score in [0, 1].</summary>
    public required float Score { get; init; }
}
