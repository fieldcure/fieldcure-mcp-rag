using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FieldCure.Mcp.Rag;

/// <summary>
/// Scans the base path for orphan KB folders and deletes them.
/// An orphan is a folder that looks like a KB (GUID name) but has no config.json.
/// Protected folders (prefixed with . or _, containing -backup-, or non-GUID names)
/// are never touched.
/// </summary>
internal static class OrphanCleanupRunner
{
    /// <summary>
    /// Default grace period applied to a folder's most-recent write timestamp before
    /// it is eligible for deletion. Closes the App-side <c>mkdir → write config.json</c>
    /// race window — a freshly created KB folder may be observed without
    /// <c>config.json</c> for a few hundred milliseconds, and prune must not delete it.
    /// </summary>
    internal static readonly TimeSpan DefaultMtimeGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Scans the base path for orphan KB folders and deletes them.
    /// </summary>
    /// <param name="basePath">Root directory containing knowledge-base folders.</param>
    /// <param name="loggerFactory">Logger factory used for cleanup diagnostics.</param>
    /// <param name="emitJson">
    /// When true, writes a JSON summary of the result to stdout (the contract used by
    /// the <c>prune-orphans</c> CLI mode). When false, the summary is reported only
    /// through the logger — required for serve-mode startup prune, where stdout is
    /// reserved for the MCP wire protocol and any extra bytes corrupt the host parser.
    /// </param>
    /// <param name="mtimeGrace">
    /// Skip folders whose last write time is newer than (now − grace). Defaults to
    /// <see cref="DefaultMtimeGrace"/>; tests pass <see cref="TimeSpan.Zero"/> to
    /// disable. Closes the App-side mkdir/config.json race.
    /// </param>
    /// <returns>Zero on success, or one when the base path is invalid.</returns>
    public static async Task<int> RunAsync(
        string basePath,
        ILoggerFactory loggerFactory,
        bool emitJson = true,
        TimeSpan? mtimeGrace = null)
    {
        var logger = loggerFactory.CreateLogger("PruneOrphans");

        if (!Directory.Exists(basePath))
        {
            logger.LogWarning("Base path does not exist: {Path}", basePath);
            return 1;
        }

        var grace = mtimeGrace ?? DefaultMtimeGrace;
        var now = DateTime.UtcNow;

        var scanned = 0;
        var orphansFound = 0;
        var skippedYoung = 0;
        var cleaned = 0;
        var failed = new List<object>();

        foreach (var dir in Directory.GetDirectories(basePath))
        {
            scanned++;

            var classification = MultiKbContext.Classify(dir, requireGuid: true);
            if (classification != FolderClassification.Orphan)
                continue;

            orphansFound++;
            var folderName = Path.GetFileName(dir);

            // mtime grace: a folder freshly created by the App may be observed
            // momentarily without config.json. Skip it on this pass; a later
            // prune (or the next serve startup) will clean it up if it really
            // is an orphan.
            if (grace > TimeSpan.Zero)
            {
                DateTime lastWrite;
                try
                {
                    lastWrite = Directory.GetLastWriteTimeUtc(dir);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read last-write time for {Folder}; skipping for safety.", folderName);
                    skippedYoung++;
                    continue;
                }

                if (now - lastWrite < grace)
                {
                    skippedYoung++;
                    logger.LogDebug(
                        "Skipping young folder {Folder} (mtime age {AgeMs}ms < grace {GraceMs}ms).",
                        folderName,
                        (int)(now - lastWrite).TotalMilliseconds,
                        (int)grace.TotalMilliseconds);
                    continue;
                }
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                cleaned++;
                logger.LogInformation("Deleted orphan folder: {Folder}", folderName);
            }
            catch (Exception ex)
            {
                failed.Add(new { folder = folderName, error = ex.Message });
                logger.LogWarning(ex, "Failed to delete orphan folder: {Folder}", folderName);
            }
        }

        if (emitJson)
        {
            var result = new
            {
                scanned,
                orphans_found = orphansFound,
                skipped_young = skippedYoung,
                cleaned,
                failed,
            };

            // Output result as JSON to stdout for callers to parse.
            // Only the prune-orphans CLI path emits this — serve-mode prune sets
            // emitJson=false because stdout is reserved for the MCP wire protocol.
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }));
        }

        logger.LogInformation(
            "Prune complete: {Scanned} scanned, {Found} orphans, {SkippedYoung} skipped (young), {Cleaned} cleaned, {Failed} failed",
            scanned, orphansFound, skippedYoung, cleaned, failed.Count);

        return 0;
    }
}
