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
/// added by AssistStudio while the orchestrator is running.
/// </summary>
internal static class ExecQueueRunner
{
    /// <summary>
    /// Runs the exec-queue orchestrator. Returns 0 on success, 1 on failure.
    /// </summary>
    public static async Task<int> RunAsync(string queueFilePath, bool verbose, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ExecQueue");
        var basePath = Path.GetDirectoryName(queueFilePath)!;

        // Acquire PID lock
        if (!TryAcquireLock(queueFilePath, logger))
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

                // Find next pending entry (not started, no error)
                var entry = queue.Entries.FirstOrDefault(e =>
                    e.StartedAt is null && e.LastError is null);

                if (entry is null)
                {
                    logger.LogInformation("No more pending entries. Orchestrator exiting.");
                    break;
                }

                var kbPath = Path.Combine(basePath, entry.KbId);
                logger.LogInformation("Processing KB: {KbId} at {Path}", entry.KbId, kbPath);

                // Mark as started
                entry.StartedAt = DateTime.UtcNow.ToString("o");
                SaveQueue(queueFilePath, queue);

                try
                {
                    var configPath = Path.Combine(kbPath, "config.json");
                    if (!File.Exists(configPath))
                    {
                        logger.LogWarning("config.json not found for {KbId}, skipping.", entry.KbId);
                        entry.LastError = "config.json not found";
                        SaveQueue(queueFilePath, queue);
                        continue;
                    }

                    var config = RagConfig.Load(kbPath);
                    var dbPath = Path.Combine(kbPath, "rag.db");

                    var entryLogger = loggerFactory.CreateLogger<IndexingEngine>();
                    var credentials = new CredentialService();
                    using var store = new SqliteVectorStore(dbPath);
                    var chunker = new TextChunker(maxChars: config.Embedding.MaxChunkChars);
                    var embeddingProvider = CreateEmbeddingProvider(config.Embedding, credentials);
                    var contextualizer = CreateContextualizer(config.Contextualizer, credentials, loggerFactory);

                    var engine = new IndexingEngine(kbPath, config, store, embeddingProvider, chunker, contextualizer, entryLogger);

                    var force = entry.IsReindex && entry.PartialMode is null;
                    var result = await engine.RunAsync(force, entry.PartialMode, CancellationToken.None);

                    logger.LogInformation("KB {KbId} finished with exit code {Code} — {Indexed} indexed, {Failed} failed",
                        entry.KbId, result.ExitCode, result.Indexed, result.Failed);

                    // Clean up cancel file
                    var cancelPath = Path.Combine(kbPath, "cancel");
                    if (File.Exists(cancelPath))
                        try { File.Delete(cancelPath); } catch { /* best-effort */ }

                    // Remove completed entry
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
            ReleaseLock(queueFilePath, logger);
        }
    }

    #region Lock Management

    private static bool TryAcquireLock(string queueFilePath, ILogger logger)
    {
        var queue = LoadQueue(queueFilePath);
        if (queue is null) return false;

        if (queue.Lock is not null)
        {
            try
            {
                var existing = Process.GetProcessById(queue.Lock.Pid);
                if (!existing.HasExited)
                {
                    logger.LogInformation("Another orchestrator (PID {Pid}) is running. Exiting.",
                        queue.Lock.Pid);
                    return false;
                }
            }
            catch (ArgumentException)
            {
                // Process not found — stale lock
            }

            logger.LogInformation("Cleaning stale lock from PID {Pid}", queue.Lock.Pid);
        }

        queue.Lock = new DeferredQueueLock
        {
            Pid = Environment.ProcessId,
            StartedAt = DateTime.UtcNow.ToString("o"),
        };
        SaveQueue(queueFilePath, queue);
        return true;
    }

    private static void ReleaseLock(string queueFilePath, ILogger logger)
    {
        try
        {
            var queue = LoadQueue(queueFilePath);
            if (queue is null) return;

            queue.Lock = null;

            if (queue.Entries.Count == 0)
            {
                // Queue is empty — delete the file entirely
                try { File.Delete(queueFilePath); } catch { /* best-effort */ }
            }
            else
            {
                SaveQueue(queueFilePath, queue);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release lock.");
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

        return new OpenAiChunkContextualizer(
            baseUrl, config.Model, apiKey, logger: loggerFactory.CreateLogger<OpenAiChunkContextualizer>());
    }

    #endregion

    #region Queue File I/O

    private static DeferredQueue? LoadQueue(string path)
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

    private static void SaveQueue(string path, DeferredQueue queue)
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
    public DeferredQueueLock? Lock { get; set; }
    public List<DeferredIndexEntry> Entries { get; set; } = [];
}

/// <summary>PID-based lock for the orchestrator.</summary>
internal sealed class DeferredQueueLock
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
    public string? StartedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Source-generated JSON context for the deferred queue.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeferredQueue))]
internal sealed partial class DeferredQueueJsonContext : JsonSerializerContext;

#endregion
