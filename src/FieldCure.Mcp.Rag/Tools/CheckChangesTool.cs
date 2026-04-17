using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using FieldCure.Mcp.Rag.Contextualization;
using FieldCure.Mcp.Rag.Indexing;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that performs a dry-run scan comparing the filesystem against the index
/// to detect added, modified, and deleted files without actually re-indexing.
/// </summary>
[McpServerToolType]
public static class CheckChangesTool
{
    [McpServerTool(Name = "check_changes", ReadOnly = true, Destructive = false, Idempotent = true),
     Description(
        "Compares source files on disk against the index to detect added, modified, " +
        "and deleted files. Does not modify the index. Lightweight metadata-only " +
        "operation (no GPU, no API calls). Use before start_reindex to determine " +
        "if re-indexing is needed. Also detects DB schema staleness and " +
        "contextualization degradation.")]
    public static async Task<string> CheckChanges(
        MultiKbContext context,
        [Description("Knowledge base ID")]
        string kb_id,
        CancellationToken cancellationToken = default)
    {
        var kb = context.GetKb(kb_id);
        var store = kb.Store;
        var config = kb.Config;

        // 1. Collect files from filesystem
        var fsFiles = CollectFiles(config.SourcePaths);

        // 2. Build storagePath → filePath map
        var fsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (filePath, sourcePath) in fsFiles)
        {
            var rel = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');
            var sourceIndex = config.SourcePaths.IndexOf(sourcePath);
            var storagePath = config.SourcePaths.Count > 1 ? $"{sourceIndex}/{rel}" : rel;
            fsMap[storagePath] = filePath;
        }

        // 3. Get indexed paths from DB
        var indexedPaths = new HashSet<string>(await store.GetIndexedPathsAsync(), StringComparer.Ordinal);

        // 4. Classify: added, deleted, possibly modified
        var addedFiles = new List<string>();
        var modifiedFiles = new List<string>();
        var deletedFiles = new List<string>();

        // 4a. Load known-failed files from last indexing
        var failedFilesJson = await store.GetMetadataAsync("last_failed_files");
        var knownFailed = failedFilesJson is not null
            ? new HashSet<string>(JsonSerializer.Deserialize<string[]>(failedFilesJson)!, StringComparer.Ordinal)
            : new HashSet<string>();

        var failedFiles = new List<string>();

        // Added: in filesystem but not in DB (excluding known-failed)
        // Possibly modified: in both — need hash comparison
        foreach (var (storagePath, filePath) in fsMap)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!indexedPaths.Contains(storagePath))
            {
                if (knownFailed.Contains(storagePath))
                    failedFiles.Add(storagePath);
                else
                    addedFiles.Add(storagePath);
            }
            else
            {
                var storedHash = await store.GetFileHashAsync(storagePath);
                var currentHash = await ComputeFileHashAsync(filePath);
                if (storedHash != currentHash)
                    modifiedFiles.Add(storagePath);
            }
        }

        // Deleted: in DB but not in filesystem
        foreach (var indexedPath in indexedPaths)
        {
            if (!fsMap.ContainsKey(indexedPath))
                deletedFiles.Add(indexedPath);
        }

        // 5. Prompt staleness check
        var storedHash2 = await store.GetMetadataAsync(ChunkContextualizerHelper.MetaKeyPromptHash);
        var defaultHash = ChunkContextualizerHelper.ComputePromptHash(
            ChunkContextualizerHelper.DefaultSystemPrompt);
        var storedPrompt = await store.GetMetadataAsync(ChunkContextualizerHelper.MetaKeySystemPrompt);
        var isPromptStale = storedHash2 is not null && storedHash2 != defaultHash && storedPrompt is null;

        // 6. Schema staleness check — read user_version from the already-open store.
        var kbSchemaVersion = store.GetUserVersion();
        var currentSchemaVersion = Storage.SqliteVectorStore.TargetUserVersion;
        var isSchemaStale = kbSchemaVersion < currentSchemaVersion;

        // 7. Contextualization degradation — any file with raw chunks.
        var ctxStats = await store.GetContextualizationStatsAsync();
        var isContextualizationDegraded = ctxStats.FilesDegraded > 0;

        var result = new
        {
            kb_id,
            added = addedFiles.Count,
            modified = modifiedFiles.Count,
            deleted = deletedFiles.Count,
            failed = failedFiles.Count,
            added_files = addedFiles,
            modified_files = modifiedFiles,
            deleted_files = deletedFiles,
            failed_files = failedFiles,
            is_prompt_stale = isPromptStale,
            is_schema_stale = isSchemaStale,
            kb_schema_version = kbSchemaVersion,
            current_schema_version = currentSchemaVersion,
            is_contextualization_degraded = isContextualizationDegraded,
            is_clean = addedFiles.Count == 0 && modifiedFiles.Count == 0
                       && deletedFiles.Count == 0 && !isPromptStale && !isSchemaStale,
        };

        return JsonSerializer.Serialize(result, McpJson.Indented);
    }

    /// <summary>Collects all supported files from the given source paths.</summary>
    static List<(string FilePath, string SourcePath)> CollectFiles(List<string> sourcePaths)
    {
        var files = new List<(string, string)>();

        foreach (var sourcePath in sourcePaths)
        {
            if (!Directory.Exists(sourcePath))
                continue;

            var found = Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                .Where(f => IndexingEngine.SupportedExtensions.Contains(Path.GetExtension(f)));

            foreach (var f in found)
                files.Add((f, sourcePath));
        }

        return files;
    }

    /// <summary>Computes a SHA-256 hash of the file contents.</summary>
    static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
