using System.ComponentModel;
using System.Text.Json;
using FieldCure.Mcp.Rag.Contextualization;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that returns index metadata for the host application.
/// </summary>
[McpServerToolType]
public static class GetIndexInfoTool
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "get_index_info", ReadOnly = true, Destructive = false, Idempotent = true),
     Description(
        "Internal tool for host application. Returns index metadata including " +
        "file/chunk counts, indexing status, and prompt hash for " +
        "stale-index detection. Do not call unless explicitly requested by the user.")]
    public static async Task<string> GetIndexInfo(
        MultiKbContext context,
        [Description("Knowledge base ID")]
        string kb_id,
        CancellationToken cancellationToken = default)
    {
        var kb = context.GetKb(kb_id);
        var store = kb.Store;

        var totalChunks = await store.GetTotalChunkCountAsync();
        var indexedPaths = await store.GetIndexedPathsAsync();

        var storedPrompt = await store.GetMetadataAsync(
            ChunkContextualizerHelper.MetaKeySystemPrompt);
        var storedHash = await store.GetMetadataAsync(
            ChunkContextualizerHelper.MetaKeyPromptHash);

        var defaultPromptHash = ChunkContextualizerHelper.ComputePromptHash(
            ChunkContextualizerHelper.DefaultSystemPrompt);

        var lockInfo = store.GetLockInfo();
        var lastIndexedAt = await store.GetLastIndexedAtAsync();

        var failedCountStr = await store.GetMetadataAsync("last_failed_count");
        var failedCount = int.TryParse(failedCountStr, out var fc) ? fc : 0;
        var failedFilesJson = await store.GetMetadataAsync("last_failed_files");
        var failedReasonsJson = await store.GetMetadataAsync("last_failed_reasons");

        var result = new
        {
            kb_id,
            kb_name = kb.Config.Name,
            folder = kb.KbPath,
            total_files = indexedPaths.Count,
            total_chunks = totalChunks,
            last_indexed_at = lastIndexedAt,
            is_indexing = lockInfo.IsIndexing,
            indexing_progress = lockInfo.IsIndexing
                ? new { current = lockInfo.Current, total = lockInfo.Total, pid = lockInfo.Pid }
                : null,
            system_prompt = storedPrompt,
            effective_prompt_hash = storedHash,
            default_prompt_hash = defaultPromptHash,
            is_prompt_stale = storedHash is not null && storedHash != defaultPromptHash && storedPrompt is null,
            last_failed_count = failedCount,
            last_failed_files = failedFilesJson is not null
                ? JsonSerializer.Deserialize<string[]>(failedFilesJson) : Array.Empty<string>(),
            last_failed_reasons = failedReasonsJson is not null
                ? JsonSerializer.Deserialize<string[]>(failedReasonsJson) : Array.Empty<string>(),
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
