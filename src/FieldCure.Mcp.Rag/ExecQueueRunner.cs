#pragma warning disable CA1416 // Platform compatibility — this tool targets Windows (AssistStudio integration)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Configuration;
using FieldCure.Mcp.Rag.Contextualization;
using FieldCure.Mcp.Rag.Credentials;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Indexing;
using FieldCure.Mcp.Rag.Storage;
using Microsoft.Extensions.Logging;

namespace FieldCure.Mcp.Rag;

/// <summary>
/// Orchestrator that consumes the deferred indexing queue sequentially.
/// Each entry is processed one at a time — no GPU contention.
/// The queue file is re-read after each entry to pick up new entries
/// added while the orchestrator is running.
/// </summary>
internal static class ExecQueueRunner
{
    internal const string LockFileName = "orchestrator.lock";
    internal const string QueueFileName = ".deferred-queue.json";

    /// <summary>
    /// Runs the exec-queue orchestrator. Returns 0 on success, 1 on failure.
    /// </summary>
    /// <param name="queueFilePath">Path to the deferred queue JSON file.</param>
    /// <param name="sweepAll">
    /// When true, processes all entries including <c>deferred=true</c> ones.
    /// Used by AssistStudio at app shutdown to flush the deferred queue.
    /// When false (default), only <c>deferred=false</c> entries are processed.
    /// </param>
    /// <param name="verbose">Enable verbose logging.</param>
    /// <param name="loggerFactory">Logger factory for creating loggers.</param>
    public static async Task<int> RunAsync(
        string queueFilePath, bool sweepAll, bool verbose, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ExecQueue");
        var basePath = Path.GetDirectoryName(queueFilePath)!;
        var lockFilePath = Path.Combine(basePath, LockFileName);

        if (!TryAcquireLock(lockFilePath, logger))
            return 0;

        try
        {
            while (true)
            {
                var queue = LoadQueue(queueFilePath);
                if (queue is null)
                {
                    logger.LogWarning("Failed to load queue file.");
                    break;
                }

                var entry = queue.Entries.FirstOrDefault(e =>
                    e.StartedAt is null
                    && e.LastError is null
                    && (sweepAll || !e.Deferred));

                if (entry is null)
                {
                    logger.LogInformation("No more pending entries. Orchestrator exiting.");
                    break;
                }

                var kbPath = Path.Combine(basePath, entry.KbId);
                var configPath = Path.Combine(kbPath, "config.json");

                // Logical deletion guard: config.json removed → skip + purge entry
                if (!File.Exists(configPath))
                {
                    logger.LogInformation("KB {KbId} was deleted (config.json missing). Removing from queue.", entry.KbId);
                    queue.Entries.RemoveAll(e => e.KbId == entry.KbId);
                    SaveQueue(queueFilePath, queue);
                    continue;
                }

                logger.LogInformation("Processing KB: {KbId} at {Path}", entry.KbId, kbPath);

                entry.StartedAt = DateTime.UtcNow.ToString("o");
                SaveQueue(queueFilePath, queue);

                try
                {
                    var config = RagConfig.Load(kbPath);
                    var dbPath = Path.Combine(kbPath, "rag.db");

                    var entryLogger = loggerFactory.CreateLogger<IndexingEngine>();
                    var credentials = new CredentialService();
                    using var store = new SqliteVectorStore(dbPath);
                    var chunker = new TextChunker(maxChars: config.Embedding.MaxChunkChars);
                    var embeddingProvider = CreateEmbeddingProvider(config.Embedding, credentials);
                    var contextualizer = CreateContextualizer(config.Contextualizer, credentials, loggerFactory);

                    var engine = new IndexingEngine(kbPath, config, store, embeddingProvider, chunker, contextualizer, entryLogger);

                    var result = await engine.RunAsync(entry.Force, entry.PartialMode, CancellationToken.None);

                    logger.LogInformation("KB {KbId} finished with exit code {Code} — {Indexed} indexed, {Failed} failed",
                        entry.KbId, result.ExitCode, result.Indexed, result.Failed);

                    var cancelPath = Path.Combine(kbPath, "cancel");
                    if (File.Exists(cancelPath))
                        try { File.Delete(cancelPath); } catch { /* best-effort */ }

                    // Re-read queue to pick up concurrent additions, then remove completed entry
                    queue = LoadQueue(queueFilePath) ?? queue;
                    queue.Entries.RemoveAll(e => e.KbId == entry.KbId);
                    SaveQueue(queueFilePath, queue);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to index KB {KbId}", entry.KbId);
                    queue = LoadQueue(queueFilePath) ?? queue;
                    var failedEntry = queue.Entries.FirstOrDefault(e => e.KbId == entry.KbId);
                    if (failedEntry is not null)
                    {
                        failedEntry.LastError = ex.Message;
                        SaveQueue(queueFilePath, queue);
                    }
                }
            }

            return 0;
        }
        finally
        {
            ReleaseLock(lockFilePath, queueFilePath, logger);
        }
    }

    #region Orchestrator Lock (file-based)

    /// <summary>
    /// Attempts to acquire the orchestrator lock via a separate lock file.
    /// Defends against PID reuse by comparing <c>started_at</c> with the
    /// process's actual <see cref="Process.StartTime"/>.
    /// </summary>
    internal static bool TryAcquireLock(string lockFilePath, ILogger logger)
    {
        if (File.Exists(lockFilePath))
        {
            try
            {
                var json = File.ReadAllText(lockFilePath);
                var existing = JsonSerializer.Deserialize(json, DeferredQueueJsonContext.Default.OrchestratorLock);

                if (existing is not null && !IsLockStale(existing))
                {
                    logger.LogInformation("Another orchestrator (PID {Pid}) is running. Exiting.", existing.Pid);
                    return false;
                }
            }
            catch
            {
                // Corrupt lock file — treat as stale
            }

            logger.LogInformation("Cleaning stale orchestrator lock.");
        }

        var lockData = new OrchestratorLock
        {
            Pid = Environment.ProcessId,
            StartedAt = DateTime.UtcNow.ToString("o"),
        };
        var lockJson = JsonSerializer.Serialize(lockData, DeferredQueueJsonContext.Default.OrchestratorLock);
        File.WriteAllText(lockFilePath, lockJson);
        return true;
    }

    /// <summary>
    /// Checks whether an orchestrator lock is held by a dead or reused PID.
    /// </summary>
    private static bool IsLockStale(OrchestratorLock lockInfo)
    {
        try
        {
            var process = Process.GetProcessById(lockInfo.Pid);
            if (process.HasExited) return true;

            // PID reuse defense: compare started_at with actual process start time.
            if (DateTime.TryParse(lockInfo.StartedAt, out var lockStarted))
            {
                var diff = Math.Abs((process.StartTime.ToUniversalTime() - lockStarted).TotalSeconds);
                if (diff > 5) return true;
            }

            return false;
        }
        catch (ArgumentException)
        {
            return true; // Process not found
        }
        catch (InvalidOperationException)
        {
            return true; // Process exited between checks
        }
    }

    private static void ReleaseLock(string lockFilePath, string queueFilePath, ILogger logger)
    {
        try
        {
            File.Delete(lockFilePath);

            // Clean up empty queue file
            var queue = LoadQueue(queueFilePath);
            if (queue is not null && queue.Entries.Count == 0)
            {
                try { File.Delete(queueFilePath); } catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release orchestrator lock.");
        }
    }

    #endregion

    #region Provider Factories

    private static IEmbeddingProvider CreateEmbeddingProvider(ProviderConfig config, ICredentialService credentials)
    {
        if (string.IsNullOrEmpty(config.Model))
            return new NullEmbeddingProvider();

        var apiKey = config.ApiKeyPreset is not null
            ? credentials.GetApiKey(config.ApiKeyPreset) ?? ""
            : "";

        var baseUrl = config.BaseUrl ?? config.Provider.ToLowerInvariant() switch
        {
            "ollama" => "http://localhost:11434",
            "openai" => "https://api.openai.com",
            _ => "http://localhost:11434",
        };

        if (config.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            return new OllamaEmbeddingProvider(
                baseUrl, config.Model,
                config.KeepAlive ?? OllamaDefaults.KeepAlive,
                config.Dimension);

        return new OpenAiCompatibleEmbeddingProvider(baseUrl, apiKey, config.Model, config.Dimension);
    }

    private static IChunkContextualizer CreateContextualizer(
        ProviderConfig config, ICredentialService credentials, ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrEmpty(config.Model))
            return new NullChunkContextualizer();

        var apiKey = config.ApiKeyPreset is not null
            ? credentials.GetApiKey(config.ApiKeyPreset) ?? ""
            : "";

        var baseUrl = config.BaseUrl ?? config.Provider.ToLowerInvariant() switch
        {
            "anthropic" => "https://api.anthropic.com",
            "ollama" => "http://localhost:11434",
            "openai" => "https://api.openai.com",
            _ => "http://localhost:11434",
        };

        if (config.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            return new AnthropicChunkContextualizer(
                apiKey, config.Model, baseUrl, logger: loggerFactory.CreateLogger<AnthropicChunkContextualizer>());

        if (config.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            return new OllamaChunkContextualizer(
                baseUrl, config.Model,
                config.KeepAlive ?? OllamaDefaults.KeepAlive,
                config.NumCtx ?? OllamaDefaults.NumCtx,
                logger: loggerFactory.CreateLogger<OllamaChunkContextualizer>());

        return new OpenAiChunkContextualizer(
            baseUrl, config.Model, apiKey, logger: loggerFactory.CreateLogger<OpenAiChunkContextualizer>());
    }

    #endregion

    #region Queue File I/O

    internal static DeferredQueue? LoadQueue(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, DeferredQueueJsonContext.Default.DeferredQueue);
        }
        catch
        {
            return null;
        }
    }

    internal static void SaveQueue(string path, DeferredQueue queue)
    {
        var json = JsonSerializer.Serialize(queue, DeferredQueueJsonContext.Default.DeferredQueue);
        File.WriteAllText(path, json);
    }

    #endregion
}

#region Queue JSON Models

/// <summary>Root object for the deferred queue JSON file.</summary>
internal sealed class DeferredQueue
{
    public int Version { get; set; } = 1;
    public List<DeferredIndexEntry> Entries { get; set; } = [];
}

/// <summary>PID-based lock stored in a separate orchestrator.lock file.</summary>
internal sealed class OrchestratorLock
{
    public int Pid { get; set; }
    public string StartedAt { get; set; } = "";
}

/// <summary>A single KB entry in the deferred queue.</summary>
internal sealed class DeferredIndexEntry
{
    public string KbId { get; set; } = "";
    public string ScheduledAt { get; set; } = "";
    public bool IsReindex { get; set; }
    public string? PartialMode { get; set; }
    public bool Force { get; set; }
    public bool Deferred { get; set; }
    public string? StartedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Source-generated JSON context for the deferred queue.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeferredQueue))]
[JsonSerializable(typeof(OrchestratorLock))]
internal sealed partial class DeferredQueueJsonContext : JsonSerializerContext;

#endregion
