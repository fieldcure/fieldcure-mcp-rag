using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that returns the full stored content for a specific chunk ID.
/// </summary>
[McpServerToolType]
public static class GetDocumentChunkTool
{
    /// <summary>
    /// Retrieves the full content and metadata for a chunk previously returned
    /// by <c>search_documents</c>.
    /// </summary>
    /// <param name="context">The shared multi-knowledge-base context.</param>
    /// <param name="kb_id">Knowledge base identifier.</param>
    /// <param name="chunk_id">Chunk identifier from a search result.</param>
    /// <param name="cancellationToken">Cancellation token for the read operation.</param>
    /// <returns>A JSON payload containing the chunk or an error object.</returns>
    [McpServerTool(Name = "get_document_chunk", ReadOnly = true, Destructive = false, Idempotent = true), Description(
        "Retrieves the full content of a specific chunk by its ID. " +
        "Use after search_documents to get complete text of a result.")]
    public static async Task<string> GetDocumentChunk(
        MultiKbContext context,
        ILogger<MultiKbContext> logger,
        [Description("Knowledge base ID")]
        string kb_id,
        [Description("Chunk ID from search results")]
        string chunk_id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var kb = context.GetKb(kb_id);
            var chunk = await kb.Store.GetChunkAsync(chunk_id);

            if (chunk is null)
                return JsonSerializer.Serialize(new { error = $"Chunk not found: {chunk_id}", kb_id });

            var response = new
            {
                kb_id,
                chunk_id = chunk.Id,
                source_path = chunk.SourcePath,
                chunk_index = chunk.ChunkIndex,
                content = chunk.Content,
                metadata = chunk.Metadata,
            };

            return JsonSerializer.Serialize(response, McpJson.Indented);
        }
        catch (Exception ex)
        {
            // Without this catch, the MCP framework wraps the exception in a
            // plain-text "An error occurred invoking 'get_document_chunk'." reply
            // that hosts cannot parse as JSON.
            logger.LogError(ex, "get_document_chunk({KbId}, {ChunkId}) failed", kb_id, chunk_id);
            return JsonSerializer.Serialize(new
            {
                kb_id,
                chunk_id,
                status = "error",
                error = $"{ex.GetType().Name}: {ex.Message}",
            }, McpJson.Indented);
        }
    }
}
