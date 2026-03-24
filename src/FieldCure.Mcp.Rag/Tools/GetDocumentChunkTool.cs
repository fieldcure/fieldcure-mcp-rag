using System.ComponentModel;
using System.Text.Json;
using FieldCure.Mcp.Rag.Storage;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

[McpServerToolType]
public static class GetDocumentChunkTool
{
    [McpServerTool(Name = "get_document_chunk"), Description(
        "Retrieves the full content of a specific chunk by its ID. " +
        "Use after search_documents to get complete text of a result.")]
    public static async Task<string> GetDocumentChunk(
        RagContext context,
        [Description("Chunk ID from search results")]
        string chunk_id,
        CancellationToken cancellationToken = default)
    {
        var chunk = await context.Store.GetChunkAsync(chunk_id);

        if (chunk is null)
            return JsonSerializer.Serialize(new { error = $"Chunk not found: {chunk_id}" });

        var response = new
        {
            chunk_id = chunk.Id,
            source_path = chunk.SourcePath,
            chunk_index = chunk.ChunkIndex,
            content = chunk.Content,
            metadata = chunk.Metadata,
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }
}
