using System.Diagnostics;
using System.Text.Json;
using FieldCure.Mcp.Rag.Configuration;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag.Tests.Tools;

/// <summary>
/// End-to-end coverage of the stale-lock recovery flow exposed via the
/// <c>start_reindex</c> MCP tool. App-free — the test drives the tool the
/// same way an MCP host would and asserts on the JSON reply, so a manual
/// repro through AssistStudio is not needed to validate the fix.
/// </summary>
[TestClass]
public class StartReindexToolStaleLockTests
{
    sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimension => 2;
        public string ModelId => "stub";
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new float[] { 1f, 0f });
    }

    static IEmbeddingProvider StubEmbedding(ProviderConfig cfg) => new StubEmbeddingProvider();

    static string CreateBasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_startreindex_stalelock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Lays down a minimal KB folder with a present (but empty) config.json.</summary>
    static string PrepareKb(string basePath, string kbId)
    {
        var kbDir = Path.Combine(basePath, kbId);
        Directory.CreateDirectory(kbDir);
        File.WriteAllText(Path.Combine(kbDir, "config.json"), "{}");
        return kbDir;
    }

    /// <summary>Writes a queue entry that pretends a previous orchestrator started indexing this KB and never cleared it.</summary>
    static void WriteStaleRunningQueue(string basePath, string kbId)
    {
        var queuePath = Path.Combine(basePath, ExecQueueRunner.QueueFileName);
        var queue = new DeferredQueue
        {
            Entries =
            [
                new DeferredIndexEntry
                {
                    KbId = kbId,
                    ScheduledAt = "2026-05-06T03:00:00Z",
                    StartedAt = "2026-05-06T03:01:00Z",
                    LastError = null,
                    Deferred = false,
                }
            ],
        };
        ExecQueueRunner.SaveQueue(queuePath, queue);
    }

    /// <summary>Writes an orchestrator.lock pinned to the test process so IsOrchestratorAlive returns true.</summary>
    static void WriteAliveLock(string basePath)
    {
        var self = Process.GetCurrentProcess();
        var data = new OrchestratorLock
        {
            Pid = self.Id,
            StartedAt = self.StartTime.ToUniversalTime().ToString("o"),
        };
        var json = JsonSerializer.Serialize(data, DeferredQueueJsonContext.Default.OrchestratorLock);
        File.WriteAllText(Path.Combine(basePath, ExecQueueRunner.LockFileName), json);
    }

    /// <summary>
    /// The exact bug reproduced and patched: a queue entry with StartedAt set
    /// and no orchestrator.lock present. Without the fix, start_reindex
    /// returned <c>"already_running"</c> indefinitely. With the fix, the
    /// stale entry is recovered (LastError marked), the request falls
    /// through the previously-failed-replace branch, and the reply is
    /// <c>"queued"</c>.
    /// </summary>
    [TestMethod]
    public void StartReindex_recoversStaleEntry_whenOrchestratorIsDead()
    {
        var basePath = CreateBasePath();
        const string kbId = "kb-stale";
        PrepareKb(basePath, kbId);
        WriteStaleRunningQueue(basePath, kbId);
        // Deliberately no orchestrator.lock — simulates crash/kill.

        using var ctx = new MultiKbContext(basePath, StubEmbedding);

        // deferred=true keeps TrySpawnOrchestrator out of the test path; we
        // only care about the queue-state transformation here.
        var json = StartReindexTool.StartReindex(
            ctx, NullLogger<MultiKbContext>.Instance, kbId, deferred: true);

        using var reply = JsonDocument.Parse(json);
        var status = reply.RootElement.GetProperty("status").GetString();
        Assert.AreNotEqual("already_running", status, "stale lock must not block a fresh request");
        Assert.AreEqual("queued", status, "recovered entry should re-queue cleanly");

        // Queue must reflect a fresh start: StartedAt cleared, LastError cleared,
        // ScheduledAt advanced. Anything else means the recovery path did not run.
        var queue = ExecQueueRunner.LoadQueue(Path.Combine(basePath, ExecQueueRunner.QueueFileName));
        Assert.IsNotNull(queue);
        var entry = queue!.Entries.Single(e => e.KbId == kbId);
        Assert.IsNull(entry.StartedAt);
        Assert.IsNull(entry.LastError);
    }

    /// <summary>
    /// Symmetric guard: when an orchestrator IS alive the stale-entry sweep
    /// must NOT fire (otherwise legitimate concurrent indexing would be
    /// disrupted). The reply must be <c>"already_running"</c> and the queue
    /// entry must keep its StartedAt mark.
    /// </summary>
    [TestMethod]
    public void StartReindex_doesNotRecover_whenOrchestratorIsAlive()
    {
        var basePath = CreateBasePath();
        const string kbId = "kb-running";
        PrepareKb(basePath, kbId);
        WriteStaleRunningQueue(basePath, kbId);
        WriteAliveLock(basePath); // an orchestrator is genuinely running

        using var ctx = new MultiKbContext(basePath, StubEmbedding);

        var json = StartReindexTool.StartReindex(
            ctx, NullLogger<MultiKbContext>.Instance, kbId, deferred: true);

        using var reply = JsonDocument.Parse(json);
        var status = reply.RootElement.GetProperty("status").GetString();
        Assert.AreEqual("already_running", status);

        // Entry must be untouched.
        var queue = ExecQueueRunner.LoadQueue(Path.Combine(basePath, ExecQueueRunner.QueueFileName));
        Assert.IsNotNull(queue);
        var entry = queue!.Entries.Single(e => e.KbId == kbId);
        Assert.AreEqual("2026-05-06T03:01:00Z", entry.StartedAt, "live orchestrator's running mark must not be cleared");
        Assert.IsNull(entry.LastError);
    }

    /// <summary>
    /// CancelReindex against a stale entry without a live orchestrator should
    /// also recover and complete the cancel — previously this returned
    /// <c>"already_running"</c> and refused to clean up.
    /// </summary>
    [TestMethod]
    public void CancelReindex_recoversStaleEntry_whenOrchestratorIsDead()
    {
        var basePath = CreateBasePath();
        const string kbId = "kb-stale-cancel";
        PrepareKb(basePath, kbId);
        WriteStaleRunningQueue(basePath, kbId);

        using var ctx = new MultiKbContext(basePath, StubEmbedding);

        var json = CancelReindexTool.CancelReindex(
            ctx, NullLogger<MultiKbContext>.Instance, kbId);

        using var reply = JsonDocument.Parse(json);
        // After recovery the entry has LastError set, so cancel can purge it.
        Assert.IsTrue(reply.RootElement.GetProperty("cancelled").GetBoolean(),
            "stale entry must be cancellable once orchestrator is dead");

        var queue = ExecQueueRunner.LoadQueue(Path.Combine(basePath, ExecQueueRunner.QueueFileName));
        Assert.IsTrue(queue is null || queue.Entries.All(e => e.KbId != kbId),
            "cancelled entry should be removed from the queue");
    }
}
