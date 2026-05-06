using System.Text;
using System.Text.Json;
using FieldCure.Mcp.Rag;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag.Tests;

/// <summary>
/// Tests for <see cref="OrphanCleanupRunner"/> — orphan classification, mtime
/// grace (App-creation race protection), and the emitJson contract that splits
/// the CLI path (writes JSON to stdout) from the serve-startup path
/// (logger-only, stdout reserved for the MCP wire protocol).
/// </summary>
[TestClass]
public class OrphanCleanupRunnerTests
{
    /// <summary>Creates a fresh, isolated base path under TEMP for one test.</summary>
    static string CreateBasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_prune_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Creates a GUID-named folder under <paramref name="basePath"/> with optional config.json.</summary>
    static string CreateKbFolder(string basePath, bool withConfig, string? name = null)
    {
        var folderName = name ?? Guid.NewGuid().ToString();
        var dir = Path.Combine(basePath, folderName);
        Directory.CreateDirectory(dir);
        if (withConfig)
            File.WriteAllText(Path.Combine(dir, "config.json"), "{\"id\":\"" + folderName + "\"}");
        return dir;
    }

    /// <summary>Backdates a directory's mtime so it falls outside the prune grace window.</summary>
    static void Age(string dir, TimeSpan age)
        => Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);

    /// <summary>
    /// Live KB (folder with config.json) survives prune even when its name is a GUID.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_PreservesLiveKb()
    {
        var basePath = CreateBasePath();
        var liveKb = CreateKbFolder(basePath, withConfig: true);

        var rc = await OrphanCleanupRunner.RunAsync(
            basePath, NullLoggerFactory.Instance, emitJson: false, mtimeGrace: TimeSpan.Zero);

        Assert.AreEqual(0, rc);
        Assert.IsTrue(Directory.Exists(liveKb), "Live KB folder should not be deleted.");
    }

    /// <summary>
    /// Aged orphan (no config.json, GUID name, mtime older than grace) is deleted.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_DeletesAgedOrphan()
    {
        var basePath = CreateBasePath();
        var orphan = CreateKbFolder(basePath, withConfig: false);
        Age(orphan, TimeSpan.FromMinutes(1));

        var rc = await OrphanCleanupRunner.RunAsync(
            basePath, NullLoggerFactory.Instance, emitJson: false);

        Assert.AreEqual(0, rc);
        Assert.IsFalse(Directory.Exists(orphan), "Aged orphan should be deleted.");
    }

    /// <summary>
    /// Young orphan (mtime within grace window) is preserved — protects against
    /// the App-side <c>mkdir → write config.json</c> race.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_SkipsYoungOrphan_WithDefaultGrace()
    {
        var basePath = CreateBasePath();
        var youngOrphan = CreateKbFolder(basePath, withConfig: false);
        // Folder was just created — mtime is "now", well within DefaultMtimeGrace.

        var rc = await OrphanCleanupRunner.RunAsync(
            basePath, NullLoggerFactory.Instance, emitJson: false);

        Assert.AreEqual(0, rc);
        Assert.IsTrue(Directory.Exists(youngOrphan), "Young orphan must survive default grace.");
    }

    /// <summary>
    /// Passing <see cref="TimeSpan.Zero"/> as grace disables the protection so even
    /// just-created orphan folders are deleted — the knob tests rely on.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_GraceZero_DeletesYoungOrphan()
    {
        var basePath = CreateBasePath();
        var youngOrphan = CreateKbFolder(basePath, withConfig: false);

        var rc = await OrphanCleanupRunner.RunAsync(
            basePath, NullLoggerFactory.Instance, emitJson: false, mtimeGrace: TimeSpan.Zero);

        Assert.AreEqual(0, rc);
        Assert.IsFalse(Directory.Exists(youngOrphan), "With grace=0 a young orphan should be deleted.");
    }

    /// <summary>
    /// Non-GUID-named folders are Protected even without config.json (prune
    /// requires a GUID name to consider deletion).
    /// </summary>
    [TestMethod]
    public async Task RunAsync_PreservesNonGuidFolder()
    {
        var basePath = CreateBasePath();
        var nonGuid = Path.Combine(basePath, "scratch");
        Directory.CreateDirectory(nonGuid);
        Age(nonGuid, TimeSpan.FromMinutes(1));

        var rc = await OrphanCleanupRunner.RunAsync(
            basePath, NullLoggerFactory.Instance, emitJson: false, mtimeGrace: TimeSpan.Zero);

        Assert.AreEqual(0, rc);
        Assert.IsTrue(Directory.Exists(nonGuid), "Non-GUID folder must never be deleted by prune.");
    }

    /// <summary>
    /// Folders prefixed with <c>.</c> / <c>_</c> or containing <c>-backup-</c> are Protected.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_PreservesProtectedFolders()
    {
        var basePath = CreateBasePath();
        var dotted = Path.Combine(basePath, ".cache");
        var under = Path.Combine(basePath, "_state");
        var backup = Path.Combine(basePath, Guid.NewGuid() + "-backup-2026");
        Directory.CreateDirectory(dotted);
        Directory.CreateDirectory(under);
        Directory.CreateDirectory(backup);
        Age(dotted, TimeSpan.FromMinutes(1));
        Age(under, TimeSpan.FromMinutes(1));
        Age(backup, TimeSpan.FromMinutes(1));

        var rc = await OrphanCleanupRunner.RunAsync(
            basePath, NullLoggerFactory.Instance, emitJson: false, mtimeGrace: TimeSpan.Zero);

        Assert.AreEqual(0, rc);
        Assert.IsTrue(Directory.Exists(dotted));
        Assert.IsTrue(Directory.Exists(under));
        Assert.IsTrue(Directory.Exists(backup));
    }

    /// <summary>
    /// emitJson=false (serve-startup contract): nothing is written to stdout.
    /// Critical because serve's stdout carries the MCP wire protocol — extra
    /// bytes corrupt the host's JSON-RPC parser.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_EmitJsonFalse_WritesNothingToStdout()
    {
        var basePath = CreateBasePath();
        var orphan = CreateKbFolder(basePath, withConfig: false);
        Age(orphan, TimeSpan.FromMinutes(1));

        var originalOut = Console.Out;
        var captured = new StringBuilder();
        try
        {
            Console.SetOut(new StringWriter(captured));
            var rc = await OrphanCleanupRunner.RunAsync(
                basePath, NullLoggerFactory.Instance, emitJson: false);
            Assert.AreEqual(0, rc);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(string.Empty, captured.ToString(),
            "serve-startup prune must keep stdout silent (MCP wire protocol owns stdout).");
    }

    /// <summary>
    /// emitJson=true (CLI contract): a parseable JSON summary lands on stdout
    /// with the expected counters. Pins the contract that scripts depend on.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_EmitJsonTrue_WritesSummaryToStdout()
    {
        var basePath = CreateBasePath();
        var orphan = CreateKbFolder(basePath, withConfig: false);
        Age(orphan, TimeSpan.FromMinutes(1));

        var originalOut = Console.Out;
        var captured = new StringBuilder();
        try
        {
            Console.SetOut(new StringWriter(captured));
            var rc = await OrphanCleanupRunner.RunAsync(
                basePath, NullLoggerFactory.Instance, emitJson: true);
            Assert.AreEqual(0, rc);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var stdout = captured.ToString().Trim();
        Assert.IsFalse(string.IsNullOrEmpty(stdout), "CLI mode must emit JSON to stdout.");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.AreEqual(1, root.GetProperty("scanned").GetInt32());
        Assert.AreEqual(1, root.GetProperty("orphans_found").GetInt32());
        Assert.AreEqual(0, root.GetProperty("skipped_young").GetInt32());
        Assert.AreEqual(1, root.GetProperty("cleaned").GetInt32());
        Assert.AreEqual(0, root.GetProperty("failed").GetArrayLength());
    }

    /// <summary>
    /// Missing base path returns exit 1 — the CLI/serve callers branch on this
    /// to decide whether anything was actually attempted.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_MissingBasePath_ReturnsOne()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rag_prune_tests", "nonexistent_" + Guid.NewGuid().ToString("N"));

        var rc = await OrphanCleanupRunner.RunAsync(
            missing, NullLoggerFactory.Instance, emitJson: false);

        Assert.AreEqual(1, rc);
    }
}
