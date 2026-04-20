using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that queues an indexing request for a knowledge base.
/// All indexing requests flow through this single entry point.
/// The orchestrator processes them sequentially — no GPU contention.
/// </summary>
[McpServerToolType]
public static class StartReindexTool
{
    /// <summary>Scope rank: higher = broader. full ⊃ contextualization ⊃ embedding.</summary>
    private static int ScopeRank(string? partialMode) => partialMode?.ToLowerInvariant() switch
    {
        "embedding" => 1,
        "contextualization" => 2,
        _ => 3, // null = full
    };

    [McpServerTool(Name = "start_reindex", ReadOnly = false, Destructive = false, Idempotent = true),
     Description(
        "Queues an indexing request for the specified knowledge base. All requests " +
        "are processed sequentially by a background orchestrator to avoid GPU " +
        "contention — the request may not start immediately if another KB is " +
        "currently indexing.\n\n" +
        "Use check_changes first to determine if re-indexing is needed.\n" +
        "Use get_index_info to poll for progress after queuing.\n\n" +
        "Do not call this unless the user explicitly asks for indexing or " +
        "a scheduled task requires it.")]
    public static string StartReindex(
        MultiKbContext context,
        ILogger<MultiKbContext> logger,
        [Description("Knowledge base ID to index")]
        string kb_id,
        [Description("Partial indexing mode: 'contextualization', 'embedding', or omit for full")]
        string? partial_mode = null,
        [Description("Force re-indexing even if no changes detected")]
        bool force = false,
        [Description("When true, only adds to queue without starting the orchestrator. " +
                     "The entry will be processed on next orchestrator run or app shutdown sweep.")]
        bool deferred = false)
    {
        var basePath = context.BasePath;
        var kbPath = Path.Combine(basePath, kb_id);
        var configPath = Path.Combine(kbPath, "config.json");

        if (!File.Exists(configPath))
        {
            return JsonSerializer.Serialize(new { status = "not_found", kb_id }, McpJson.Indented);
        }

        var queueFilePath = Path.Combine(basePath, ExecQueueRunner.QueueFileName);
        var queue = ExecQueueRunner.LoadQueue(queueFilePath) ?? new DeferredQueue();

        var existingEntry = queue.Entries.FirstOrDefault(e => e.KbId == kb_id);
        string status;

        if (existingEntry is not null)
        {
            if (existingEntry.StartedAt is not null && existingEntry.LastError is null)
            {
                // Currently running
                var existingRank = ScopeRank(existingEntry.PartialMode);
                var newRank = ScopeRank(partial_mode);

                if (existingRank >= newRank)
                {
                    status = "already_running";
                }
                else
                {
                    // Running scope is narrower → queue a follow-up after current finishes
                    queue.Entries.Add(new DeferredIndexEntry
                    {
                        KbId = kb_id,
                        ScheduledAt = DateTime.UtcNow.ToString("o"),
                        IsReindex = partial_mode is null,
                        PartialMode = partial_mode,
                        Force = force,
                        Deferred = deferred,
                    });
                    ExecQueueRunner.SaveQueue(queueFilePath, queue);
                    status = "queued_after_current";
                }
            }
            else if (existingEntry.LastError is not null)
            {
                // Previously failed — replace with new request
                existingEntry.ScheduledAt = DateTime.UtcNow.ToString("o");
                existingEntry.IsReindex = partial_mode is null;
                existingEntry.PartialMode = partial_mode;
                existingEntry.Force = force || existingEntry.Force;
                existingEntry.Deferred = deferred;
                existingEntry.StartedAt = null;
                existingEntry.LastError = null;
                ExecQueueRunner.SaveQueue(queueFilePath, queue);
                status = "queued";
            }
            else
            {
                // Pending (not started)
                var existingRank = ScopeRank(existingEntry.PartialMode);
                var newRank = ScopeRank(partial_mode);

                if (existingRank >= newRank)
                {
                    // Existing scope is same or broader — merge force/deferred flags only
                    if (force) existingEntry.Force = true;
                    if (!deferred) existingEntry.Deferred = false;
                    ExecQueueRunner.SaveQueue(queueFilePath, queue);
                    status = "already_queued";
                }
                else
                {
                    // New scope is broader → upgrade
                    existingEntry.PartialMode = partial_mode;
                    existingEntry.IsReindex = partial_mode is null;
                    existingEntry.ScheduledAt = DateTime.UtcNow.ToString("o");
                    existingEntry.Force = force || existingEntry.Force;
                    existingEntry.Deferred = deferred;
                    ExecQueueRunner.SaveQueue(queueFilePath, queue);
                    status = "upgraded";
                }
            }
        }
        else
        {
            // New entry
            queue.Entries.Add(new DeferredIndexEntry
            {
                KbId = kb_id,
                ScheduledAt = DateTime.UtcNow.ToString("o"),
                IsReindex = partial_mode is null,
                PartialMode = partial_mode,
                Force = force,
                Deferred = deferred,
            });
            ExecQueueRunner.SaveQueue(queueFilePath, queue);
            status = "queued";
        }

        // Spawn orchestrator if this is an immediate request.
        // Covers: new entry, upgraded scope, deferred→immediate transition,
        // and queued-after-current (new entry behind running one).
        if (!deferred && status is not "not_found" and not "already_running")
        {
            TrySpawnOrchestrator(basePath, queueFilePath, logger);
        }

        // Compute queue position
        var position = 0;
        var reloaded = ExecQueueRunner.LoadQueue(queueFilePath);
        if (reloaded is not null)
        {
            var pending = reloaded.Entries.Where(e => e.StartedAt is null && e.LastError is null).ToList();
            var idx = pending.FindIndex(e => e.KbId == kb_id);
            if (idx >= 0) position = idx + 1;

            // Count running entry as position 0
            if (reloaded.Entries.Any(e => e.StartedAt is not null && e.LastError is null && e.KbId == kb_id))
                position = 0;
        }

        return JsonSerializer.Serialize(new { status, kb_id, queue_position = position }, McpJson.Indented);
    }

    /// <summary>
    /// Best-effort spawn of the sequential exec-queue orchestrator in a detached
    /// process. No-ops when an existing lock file indicates another orchestrator
    /// is already running.
    /// </summary>
    /// <param name="basePath">Root directory containing the queue state.</param>
    /// <param name="queueFilePath">Absolute path to the queue file the orchestrator should consume.</param>
    /// <param name="logger">Logger used for spawn diagnostics.</param>
    private static void TrySpawnOrchestrator(string basePath, string queueFilePath, ILogger logger)
    {
        var lockFilePath = Path.Combine(basePath, ExecQueueRunner.LockFileName);

        // Check if orchestrator is already running
        if (File.Exists(lockFilePath))
        {
            try
            {
                var json = File.ReadAllText(lockFilePath);
                var lockInfo = JsonSerializer.Deserialize(json, DeferredQueueJsonContext.Default.OrchestratorLock);
                if (lockInfo is not null)
                {
                    try
                    {
                        var process = Process.GetProcessById(lockInfo.Pid);
                        if (!process.HasExited)
                        {
                            logger.LogInformation(
                                "Orchestrator already running (PID {Pid}). New entry will be picked up dynamically.",
                                lockInfo.Pid);
                            return;
                        }
                    }
                    catch (ArgumentException) { /* dead PID */ }
                    catch (InvalidOperationException) { /* exited between checks */ }
                }
            }
            catch { /* corrupt lock file — proceed to spawn */ }
        }

        var exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            logger.LogWarning("Cannot determine process path for orchestrator spawn.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"exec-queue --queue-file \"{queueFilePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            logger.LogInformation("Spawned orchestrator: exec-queue --queue-file \"{Path}\"", queueFilePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to spawn orchestrator.");
        }
    }
}
