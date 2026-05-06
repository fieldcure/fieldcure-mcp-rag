using System.Diagnostics;
using System.Text.Json;
using FieldCure.Mcp.Rag;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag.Tests;

/// <summary>
/// Regression tests for the stale-orchestrator-lock recovery path.
/// A previous orchestrator that crashed/was killed before clearing its
/// queue entry's <c>StartedAt</c> would block all future <c>start_reindex</c>
/// calls with <c>"already_running"</c> — these tests pin the recovery
/// behavior that closes that hole.
/// </summary>
[TestClass]
public class ExecQueueRunnerTests
{
    static string CreateBasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_execqueue_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes a queue file with a single entry pre-marked as running.</summary>
    static string WriteStaleQueue(string basePath, string kbId)
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
                }
            ],
        };
        ExecQueueRunner.SaveQueue(queuePath, queue);
        return queuePath;
    }

    /// <summary>Writes an orchestrator.lock file with the given PID/start-time.</summary>
    static void WriteLock(string basePath, int pid, string startedAtIso)
    {
        var lockPath = Path.Combine(basePath, ExecQueueRunner.LockFileName);
        var data = new OrchestratorLock { Pid = pid, StartedAt = startedAtIso };
        var json = JsonSerializer.Serialize(data, DeferredQueueJsonContext.Default.OrchestratorLock);
        File.WriteAllText(lockPath, json);
    }

    /// <summary>
    /// Stale running entry without any orchestrator.lock file — recovery clears
    /// StartedAt and sets LastError so the next start_reindex falls through the
    /// "previously failed" replace branch instead of bouncing on already_running.
    /// </summary>
    [TestMethod]
    public void RecoverStaleRunningEntries_clearsStartedAt_andSetsLastError()
    {
        var basePath = CreateBasePath();
        var queuePath = WriteStaleQueue(basePath, "kb-test");

        var recovered = ExecQueueRunner.RecoverStaleRunningEntries(queuePath, NullLogger.Instance);

        Assert.AreEqual(1, recovered);
        var queue = ExecQueueRunner.LoadQueue(queuePath);
        Assert.IsNotNull(queue);
        var entry = queue!.Entries.Single();
        Assert.IsNull(entry.StartedAt, "stale StartedAt should be cleared");
        Assert.AreEqual("orchestrator_died_or_killed", entry.LastError);
    }

    /// <summary>Recovery is a no-op when nothing is marked running.</summary>
    [TestMethod]
    public void RecoverStaleRunningEntries_returnsZero_whenNoStaleEntries()
    {
        var basePath = CreateBasePath();
        var queuePath = Path.Combine(basePath, ExecQueueRunner.QueueFileName);
        ExecQueueRunner.SaveQueue(queuePath, new DeferredQueue
        {
            Entries =
            [
                new DeferredIndexEntry
                {
                    KbId = "kb-pending",
                    ScheduledAt = "2026-05-06T03:00:00Z",
                    StartedAt = null,
                }
            ],
        });

        var recovered = ExecQueueRunner.RecoverStaleRunningEntries(queuePath, NullLogger.Instance);

        Assert.AreEqual(0, recovered);
    }

    /// <summary>
    /// Entries that already carry a LastError were processed but failed —
    /// they belong to the "previously failed" replace branch and must not be
    /// touched by the stale-running sweep.
    /// </summary>
    [TestMethod]
    public void RecoverStaleRunningEntries_doesNotTouchEntriesWithLastError()
    {
        var basePath = CreateBasePath();
        var queuePath = Path.Combine(basePath, ExecQueueRunner.QueueFileName);
        ExecQueueRunner.SaveQueue(queuePath, new DeferredQueue
        {
            Entries =
            [
                new DeferredIndexEntry
                {
                    KbId = "kb-failed",
                    ScheduledAt = "2026-05-06T03:00:00Z",
                    StartedAt = "2026-05-06T03:01:00Z",
                    LastError = "embed_provider_unreachable",
                }
            ],
        });

        var recovered = ExecQueueRunner.RecoverStaleRunningEntries(queuePath, NullLogger.Instance);

        Assert.AreEqual(0, recovered);
        var entry = ExecQueueRunner.LoadQueue(queuePath)!.Entries.Single();
        Assert.AreEqual("embed_provider_unreachable", entry.LastError);
        Assert.AreEqual("2026-05-06T03:01:00Z", entry.StartedAt);
    }

    /// <summary>No lock file at all → no orchestrator alive.</summary>
    [TestMethod]
    public void IsOrchestratorAlive_returnsFalse_whenLockFileMissing()
    {
        var basePath = CreateBasePath();
        Assert.IsFalse(ExecQueueRunner.IsOrchestratorAlive(basePath));
    }

    /// <summary>Lock file is malformed (corrupt JSON) → treat as no orchestrator.</summary>
    [TestMethod]
    public void IsOrchestratorAlive_returnsFalse_whenLockFileCorrupt()
    {
        var basePath = CreateBasePath();
        File.WriteAllText(Path.Combine(basePath, ExecQueueRunner.LockFileName), "not json");
        Assert.IsFalse(ExecQueueRunner.IsOrchestratorAlive(basePath));
    }

    /// <summary>
    /// Lock points at a PID that no longer exists. PID 1 minus a large offset
    /// is overwhelmingly unlikely to be in use, especially with a clearly
    /// invalid start-time (epoch). Treated as stale → not alive.
    /// </summary>
    [TestMethod]
    public void IsOrchestratorAlive_returnsFalse_whenPidIsDead()
    {
        var basePath = CreateBasePath();
        // Use a near-impossible PID. Range varies per OS; pick a value that
        // won't match a current process and rely on Process.GetProcessById
        // to throw ArgumentException, which the helper treats as stale.
        WriteLock(basePath, pid: 0x7FFFFFFE, startedAtIso: "1970-01-01T00:00:00Z");
        Assert.IsFalse(ExecQueueRunner.IsOrchestratorAlive(basePath));
    }

    /// <summary>
    /// Lock points at the current test process with a matching start-time
    /// (within the 5-second skew tolerance). The helper must report alive,
    /// otherwise StartReindexTool would over-eagerly recover real running
    /// entries during legitimate concurrent indexing.
    /// </summary>
    [TestMethod]
    public void IsOrchestratorAlive_returnsTrue_whenLockMatchesCurrentProcess()
    {
        var basePath = CreateBasePath();
        var self = Process.GetCurrentProcess();
        var startedAt = self.StartTime.ToUniversalTime().ToString("o");
        WriteLock(basePath, pid: self.Id, startedAtIso: startedAt);
        Assert.IsTrue(ExecQueueRunner.IsOrchestratorAlive(basePath));
    }
}
