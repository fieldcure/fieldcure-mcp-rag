using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that lists all available knowledge bases.
/// Scans the base path for KB folders with config.json.
/// Also cleans up cached instances for deleted KBs.
/// </summary>
[McpServerToolType]
public static class ListKnowledgeBasesTool
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "list_knowledge_bases"),
     Description(
        "Lists all available knowledge bases with their status. " +
        "Returns ID, name, file/chunk counts, and indexing status for each KB.")]
    public static string ListKnowledgeBases(MultiKbContext context)
    {
        var kbs = context.ListKbs();

        var response = new
        {
            knowledge_bases = kbs.Select(kb => new
            {
                id = kb.Id,
                name = kb.Name,
                total_files = kb.TotalFiles,
                total_chunks = kb.TotalChunks,
                is_indexing = kb.IsIndexing,
            }),
            total = kbs.Count,
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }
}
