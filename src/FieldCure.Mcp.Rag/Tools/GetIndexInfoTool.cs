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
    [McpServerTool(Name = "get_index_info", ReadOnly = true, Destructive = false, Idempotent = true),
     Description(
        "Returns index metadata for a knowledge base: file/chunk counts, indexing " +
        "status, prompt hash for stale-index detection, and queue state " +
        "(position, deferred, last_error). Use after start_reindex to poll for " +
        "progress. The queue field shows pending/running/failed status.")]
    /// <summary>
    /// Returns indexing, schema, prompt, and queue metadata for one knowledge base.
    /// </summary>
    /// <param name="context">The shared multi-knowledge-base context.</param>
    /// <param name="kb_id">Knowledge base identifier.</param>
    /// <param name="cancellationToken">Cancellation token for the read operation.</param>
    /// <returns>A JSON payload containing current index metadata and queue state.</returns>
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

        // v1.4.3 contextualization stats (from file_index aggregate)
        var ctxStats = await store.GetContextualizationStatsAsync();

        // v1.4 metadata
        var indexedCountStr = await store.GetMetadataAsync("last_indexed_count");
        var skippedCountStr = await store.GetMetadataAsync("last_skipped_count");
        var degradedCountStr = await store.GetMetadataAsync("last_degraded_count");
        var deferredCountStr = await store.GetMetadataAsync("last_partially_deferred_count");
        var durationMsStr = await store.GetMetadataAsync("last_run_duration_ms");
        var runCompletedUtc = await store.GetMetadataAsync("last_run_completed_utc");
        var providerHealthStr = await store.GetMetadataAsync("last_provider_health");

        // Queue state
        var queueFilePath = Path.Combine(context.BasePath, ExecQueueRunner.QueueFileName);
        var queue = ExecQueueRunner.LoadQueue(queueFilePath);
        var queueEntry = queue?.Entries.FirstOrDefault(e => e.KbId == kb_id);

        object? queueInfo = null;
        string status;

        if (lockInfo.IsIndexing)
        {
            status = "indexing";
        }
        else if (queueEntry is not null)
        {
            if (queueEntry.LastError is not null)
            {
                status = "failed";
            }
            else
            {
                status = "queued";
            }

            var pendingEntries = queue!.Entries
                .Where(e => e.StartedAt is null && e.LastError is null)
                .ToList();
            var position = pendingEntries.FindIndex(e => e.KbId == kb_id) + 1;

            queueInfo = new
            {
                position,
                deferred = queueEntry.Deferred,
                partial_mode = queueEntry.PartialMode,
                scheduled_at = queueEntry.ScheduledAt,
                last_error = queueEntry.LastError,
            };
        }
        else
        {
            status = "ready";
        }

        var result = new
        {
            kb_id,
            kb_name = kb.Config.Name,
            folder = kb.KbPath,
            status,
            total_files = indexedPaths.Count,
            total_chunks = totalChunks,
            last_indexed_at = lastIndexedAt,
            is_indexing = lockInfo.IsIndexing,
            indexing_progress = lockInfo.IsIndexing
                ? new { current = lockInfo.Current, total = lockInfo.Total, pid = lockInfo.Pid }
                : null,
            queue = queueInfo,
            system_prompt = storedPrompt,
            effective_prompt_hash = storedHash,
            default_prompt_hash = defaultPromptHash,
            is_prompt_stale = storedHash is not null && storedHash != defaultPromptHash && storedPrompt is null,
            last_failed_count = failedCount,
            last_failed_files = failedFilesJson is not null
                ? JsonSerializer.Deserialize<string[]>(failedFilesJson) : Array.Empty<string>(),
            last_failed_reasons = failedReasonsJson is not null
                ? JsonSerializer.Deserialize<string[]>(failedReasonsJson) : Array.Empty<string>(),
            // v1.4 fields
            last_indexed_count = int.TryParse(indexedCountStr, out var ic) ? ic : (int?)null,
            last_skipped_count = int.TryParse(skippedCountStr, out var sc) ? sc : (int?)null,
            last_degraded_count = int.TryParse(degradedCountStr, out var dc) ? dc : (int?)null,
            last_partially_deferred_count = int.TryParse(deferredCountStr, out var pd) ? pd : (int?)null,
            last_run_duration_ms = int.TryParse(durationMsStr, out var dm) ? dm : (int?)null,
            last_run_completed_utc = runCompletedUtc,
            last_provider_health = int.TryParse(providerHealthStr, out var ph) ? ph : (int?)null,
            // v1.4.3 contextualization health
            total_chunks_contextualized = ctxStats.TotalContextualized,
            total_chunks_raw = ctxStats.TotalRaw,
            files_contextualization_degraded = ctxStats.FilesDegraded,
        };

        return JsonSerializer.Serialize(result, McpJson.Indented);
    }
}
