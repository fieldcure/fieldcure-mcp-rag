using System.ComponentModel;
using System.Text.Json;
using FieldCure.Mcp.Rag.Models;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

[McpServerToolType]
public static class SearchDocumentsTool
{
    [McpServerTool(Name = "search_documents", ReadOnly = true, Destructive = false, Idempotent = true), Description(
        "Searches documents in a knowledge base using hybrid BM25 keyword + vector semantic search. " +
        "Returns ranked results with source file and content preview.")]
    public static async Task<string> SearchDocuments(
        MultiKbContext context,
        [Description("Knowledge base ID")]
        string kb_id,
        [Description("Natural language search query")]
        string query,
        [Description("Maximum number of results (default: 5)")]
        int top_k = 5,
        [Description("Minimum similarity score 0-1 (default: 0.3)")]
        float threshold = 0.3f,
        CancellationToken cancellationToken = default)
    {
        var kb = context.GetKb(kb_id);
        var hybrid = await kb.Searcher.SearchAsync(query, top_k, threshold, cancellationToken);

        var response = new
        {
            kb_id,
            results = hybrid.Results.Select(r => new
            {
                chunk_id = r.ChunkId,
                source_path = r.SourcePath,
                chunk_index = r.ChunkIndex,
                total_chunks = r.TotalChunks,
                content = r.Content,
                score = r.Score,
                has_previous = r.ChunkIndex > 0,
                has_next = r.TotalChunks > 0 && r.ChunkIndex < r.TotalChunks - 1,
            }),
            query,
            search_mode = hybrid.Mode,
            total_chunks_searched = hybrid.TotalChunksSearched,
        };

        return JsonSerializer.Serialize(response, McpJson.Search);
    }
}
