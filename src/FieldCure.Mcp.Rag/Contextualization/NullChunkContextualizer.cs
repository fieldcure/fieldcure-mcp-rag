using FieldCure.Mcp.Rag.Models;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// No-op contextualizer that returns the original chunk text unchanged.
/// Used when no contextualizer model is configured (default).
/// </summary>
public sealed class NullChunkContextualizer : IChunkContextualizer
{
    /// <inheritdoc />
    public string SystemPrompt
    {
        get => ChunkContextualizerHelper.DefaultSystemPrompt;
        set { } // No-op: null contextualizer doesn't use a prompt
    }

    /// <inheritdoc />
    public Task<EnrichResult> EnrichAsync(
        string chunkText,
        string? documentContext,
        string sourceFileName,
        int chunkIndex,
        int totalChunks,
        CancellationToken ct = default)
    {
        return Task.FromResult(EnrichResult.Success(chunkText));
    }
}
