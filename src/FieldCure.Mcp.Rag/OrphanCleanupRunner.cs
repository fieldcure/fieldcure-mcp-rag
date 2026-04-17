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
    public static async Task<int> RunAsync(string basePath, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PruneOrphans");

        if (!Directory.Exists(basePath))
        {
            logger.LogWarning("Base path does not exist: {Path}", basePath);
            return 1;
        }

        var scanned = 0;
        var orphansFound = 0;
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

        var result = new
        {
            scanned,
            orphans_found = orphansFound,
            cleaned,
            failed,
        };

        // Output result as JSON to stdout for callers to parse
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }));

        logger.LogInformation("Prune complete: {Scanned} scanned, {Found} orphans, {Cleaned} cleaned, {Failed} failed",
            scanned, orphansFound, cleaned, failed.Count);

        return 0;
    }
}
