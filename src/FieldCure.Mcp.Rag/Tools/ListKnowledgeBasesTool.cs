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
    [McpServerTool(Name = "list_knowledge_bases", ReadOnly = true, Destructive = false, Idempotent = true),
     Description(
        "Lists all available knowledge bases with their status. " +
        "Returns ID, name, file/chunk counts, indexing status, and schema version for each KB. " +
        "A KB with is_schema_stale=true still serves search queries correctly; " +
        "re-indexing triggers automatic schema migration through the exec path.")]
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
                schema_version = kb.SchemaVersion,
                is_schema_stale = kb.IsSchemaStale,
            }),
            current_schema_version = Storage.SqliteVectorStore.TargetUserVersion,
            total = kbs.Count,
        };

        return JsonSerializer.Serialize(response, McpJson.Indented);
    }
}
