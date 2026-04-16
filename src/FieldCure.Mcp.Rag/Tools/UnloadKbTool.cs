using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that evicts a cached KB instance so its SQLite handle is released.
/// Called by the host before deleting a KB's directory on disk.
/// </summary>
[McpServerToolType]
public static class UnloadKbTool
{
    [McpServerTool(Name = "unload_kb", ReadOnly = false, Destructive = false, Idempotent = true),
     Description(
        "Releases all handles (SQLite connection, caches) for a knowledge base. " +
        "Call before deleting KB files on disk. The KB will be lazy-reloaded on next access.")]
    public static string UnloadKb(
        MultiKbContext context,
        [Description("Knowledge base ID to unload")]
        string kb_id)
    {
        context.UnloadKb(kb_id);
        return JsonSerializer.Serialize(new { kb_id, unloaded = true });
    }
}
