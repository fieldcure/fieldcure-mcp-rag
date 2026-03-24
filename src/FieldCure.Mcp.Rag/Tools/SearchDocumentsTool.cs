using System.ComponentModel;
using System.Text.Json;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Storage;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

[McpServerToolType]
public static class SearchDocumentsTool
{
    [McpServerTool(Name = "search_documents"), Description(
        "Searches the vector index for chunks semantically similar to the query. " +
        "Returns ranked results with source file and content preview.")]
    public static async Task<string> SearchDocuments(
        RagContext context,
        [Description("Natural language search query")]
        string query,
        [Description("Maximum number of results (default: 5)")]
        int top_k = 5,
        [Description("Minimum cosine similarity score 0-1 (default: 0.5)")]
        float threshold = 0.5f,
        CancellationToken cancellationToken = default)
    {
        var queryEmbedding = await context.EmbeddingProvider.EmbedAsync(query, cancellationToken);
        var results = await context.Store.SearchAsync(queryEmbedding, top_k, threshold);
        var totalChunks = await context.Store.GetTotalChunkCountAsync();

        var response = new
        {
            results = results.Select(r => new
            {
                chunk_id = r.ChunkId,
                source_path = r.SourcePath,
                chunk_index = r.ChunkIndex,
                content = r.Content,
                score = r.Score,
            }),
            query,
            total_chunks_searched = totalChunks,
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }
}
