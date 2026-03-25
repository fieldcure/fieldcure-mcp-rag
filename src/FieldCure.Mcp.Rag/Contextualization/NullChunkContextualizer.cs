namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// No-op contextualizer that returns the original chunk text unchanged.
/// Used when no contextualizer model is configured (default).
/// </summary>
public sealed class NullChunkContextualizer : IChunkContextualizer
{
    public Task<string> EnrichAsync(
        string chunkText,
        string? documentContext,
        string sourceFileName,
        int chunkIndex,
        int totalChunks,
        CancellationToken ct = default)
    {
        return Task.FromResult(chunkText);
    }
}
