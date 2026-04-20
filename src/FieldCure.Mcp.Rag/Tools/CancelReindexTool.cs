using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that removes a pending (not-yet-started) queue entry for a knowledge base.
/// </summary>
[McpServerToolType]
public static class CancelReindexTool
{
    [McpServerTool(Name = "cancel_reindex", ReadOnly = false, Destructive = false, Idempotent = true),
     Description(
        "Removes a pending indexing request from the queue. No-op if the entry " +
        "is already running or does not exist. Cannot cancel an in-progress run — " +
        "use the cancel file mechanism for that.")]
    public static string CancelReindex(
        MultiKbContext context,
        [Description("Knowledge base ID to cancel")]
        string kb_id)
    {
        var queueFilePath = Path.Combine(context.BasePath, ExecQueueRunner.QueueFileName);
        var queue = ExecQueueRunner.LoadQueue(queueFilePath);

        if (queue is null)
            return JsonSerializer.Serialize(new { kb_id, cancelled = false, reason = "no_queue" }, McpJson.Indented);

        var entry = queue.Entries.FirstOrDefault(e => e.KbId == kb_id);

        if (entry is null)
            return JsonSerializer.Serialize(new { kb_id, cancelled = false, reason = "not_found" }, McpJson.Indented);

        if (entry.StartedAt is not null && entry.LastError is null)
            return JsonSerializer.Serialize(new { kb_id, cancelled = false, reason = "already_running" }, McpJson.Indented);

        queue.Entries.RemoveAll(e => e.KbId == kb_id);
        ExecQueueRunner.SaveQueue(queueFilePath, queue);

        return JsonSerializer.Serialize(new { kb_id, cancelled = true }, McpJson.Indented);
    }
}
