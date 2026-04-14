using FieldCure.Mcp.Rag.Models;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// Enriches a text chunk with document context and normalized keywords
/// for improved search indexing. Called once per chunk during indexing.
/// </summary>
public interface IChunkContextualizer
{
    /// <summary>
    /// Gets or sets the system prompt used for chunk enrichment.
    /// Set to null or empty to use the built-in default.
    /// </summary>
    string SystemPrompt { get; set; }

    /// <summary>
    /// Enriches a chunk with contextual information.
    /// </summary>
    /// <param name="chunkText">Original chunk text.</param>
    /// <param name="documentContext">
    /// Surrounding context — full document text or summary.
    /// May be null if document is too large.
    /// </param>
    /// <param name="sourceFileName">Source file name for additional context.</param>
    /// <param name="chunkIndex">Zero-based chunk index within the document.</param>
    /// <param name="totalChunks">Total number of chunks in the document.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="EnrichResult"/> containing the text to index and whether
    /// contextualization succeeded. On failure the original text is returned
    /// with <see cref="EnrichResult.IsContextualized"/> = false.
    /// </returns>
    Task<EnrichResult> EnrichAsync(
        string chunkText,
        string? documentContext,
        string sourceFileName,
        int chunkIndex,
        int totalChunks,
        CancellationToken ct = default);
}
