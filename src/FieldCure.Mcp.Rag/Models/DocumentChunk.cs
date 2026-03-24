namespace FieldCure.Mcp.Rag.Models;

/// <summary>Represents a single indexed chunk from a source document.</summary>
public record DocumentChunk
{
    /// <summary>Unique identifier: "{sourcePathHash}_{chunkIndex}"</summary>
    public required string Id { get; init; }

    /// <summary>Relative path from context_folder root.</summary>
    public required string SourcePath { get; init; }

    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }
    public int CharOffset { get; init; }
    public string Metadata { get; init; } = "{}";
}
