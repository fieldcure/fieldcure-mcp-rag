using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

[McpServerToolType]
public static class GetDocumentChunkTool
{
    [McpServerTool(Name = "get_document_chunk", ReadOnly = true, Destructive = false, Idempotent = true), Description(
        "Retrieves the full content of a specific chunk by its ID. " +
        "Use after search_documents to get complete text of a result.")]
    public static async Task<string> GetDocumentChunk(
        MultiKbContext context,
        [Description("Knowledge base ID")]
        string kb_id,
        [Description("Chunk ID from search results")]
        string chunk_id,
        CancellationToken cancellationToken = default)
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

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }
}
