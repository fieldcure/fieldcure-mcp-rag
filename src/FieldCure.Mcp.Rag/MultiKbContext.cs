using System.Collections.Concurrent;
using FieldCure.Mcp.Rag.Configuration;
using FieldCure.Mcp.Rag.Credentials;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Search;
using FieldCure.Mcp.Rag.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag;

/// <summary>
/// Manages multiple knowledge bases under a shared base path.
/// Lazy-loads <see cref="KbInstance"/> per KB on first access.
/// Registered as a singleton in the DI container for serve mode.
/// </summary>
public sealed class MultiKbContext : IDisposable
{
    readonly string _basePath;
    readonly ICredentialService _credentials;
    readonly Func<ProviderConfig, ICredentialService, IEmbeddingProvider> _embeddingFactory;
    readonly ILogger<MultiKbContext> _logger;
    readonly ConcurrentDictionary<string, KbInstance> _instances = new();

    public MultiKbContext(
        string basePath,
        ICredentialService credentials,
        Func<ProviderConfig, ICredentialService, IEmbeddingProvider> embeddingFactory,
        ILogger<MultiKbContext>? logger = null)
    {
        _basePath = basePath;
        _credentials = credentials;
        _embeddingFactory = embeddingFactory;
        _logger = logger ?? NullLogger<MultiKbContext>.Instance;
    }

    /// <summary>
    /// Gets or creates a <see cref="KbInstance"/> for the given KB ID.
    /// Throws if the KB folder or config.json does not exist.
    /// </summary>
    public KbInstance GetKb(string kbId)
    {
        return _instances.GetOrAdd(kbId, id =>
        {
            var kbPath = Path.Combine(_basePath, id);
            if (!Directory.Exists(kbPath))
                throw new DirectoryNotFoundException($"Knowledge base not found: {id}");

            var config = RagConfig.Load(kbPath);
            var dbPath = Path.Combine(kbPath, "rag.db");

            if (!File.Exists(dbPath))
                throw new FileNotFoundException($"Database not found for knowledge base: {id}");

            var store = new SqliteVectorStore(dbPath, readOnly: true);
            var embeddingProvider = _embeddingFactory(config.Embedding, _credentials);
            var searcher = new HybridSearcher(store, embeddingProvider);

            return new KbInstance(id, kbPath, config, store, searcher);
        });
    }

    /// <summary>
    /// Evicts a cached KB instance and disposes its SQLite connection.
    /// No-op if the KB is not loaded. The next <see cref="GetKb"/> call
    /// will lazy-reload it (or fail with "not found" if deleted).
    /// </summary>
    public void UnloadKb(string kbId)
    {
        if (_instances.TryRemove(kbId, out var removed))
        {
            removed.Dispose();
            _logger.LogInformation("Unloaded KB {KbId}", kbId);
        }
    }

    /// <summary>
    /// Lists all knowledge bases by scanning the base path for folders with config.json.
    /// Applies four guards per folder (prefix, config.json existence, parseability,
    /// id ↔ folder name match) to avoid misidentifying user backup folders or broken
    /// copies as knowledge bases. Cleans up cached instances for deleted KBs.
    /// </summary>
    public IReadOnlyList<KbSummary> ListKbs()
    {
        var summaries = new List<KbSummary>();

        if (!Directory.Exists(_basePath))
            return summaries;

        var existingIds = new HashSet<string>();

        foreach (var dir in Directory.GetDirectories(_basePath))
        {
            var folderName = Path.GetFileName(dir);

            // Guard 1: skip prefix-marked folders (backups, tmp, hidden) silently.
            if (folderName.StartsWith('.') || folderName.StartsWith('_'))
                continue;

            // Guard 2: config.json must exist.
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath))
                continue;

            // Guard 3: config.json must parse.
            RagConfig config;
            try
            {
                config = RagConfig.Load(dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to parse config.json in {Folder}: {Error}",
                    folderName, ex.Message);
                continue;
            }

            // Guard 4: folder name must match config.Id (case-insensitive).
            // Mismatches usually indicate a copy/backup created outside the app.
            if (!string.Equals(folderName, config.Id, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "KB folder name '{FolderName}' does not match config id '{ConfigId}', likely a copy/backup. Skipping.",
                    folderName, config.Id);
                continue;
            }

            existingIds.Add(folderName);

            var dbPath = Path.Combine(dir, "rag.db");
            int totalFiles = 0, totalChunks = 0;
            bool isIndexing = false;
            int schemaVersion = 0;

            if (File.Exists(dbPath))
            {
                try
                {
                    using var store = new SqliteVectorStore(dbPath, readOnly: true);
                    totalFiles = store.GetIndexedPathsAsync().GetAwaiter().GetResult().Count;
                    totalChunks = store.GetTotalChunkCountAsync().GetAwaiter().GetResult();
                    var lockInfo = store.GetLockInfo();
                    isIndexing = lockInfo.IsIndexing;
                    // Piggyback on the already-open store — PRAGMA user_version
                    // reads from page 0 which is already in memory. Microseconds.
                    schemaVersion = store.GetUserVersion();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "Failed to read stats from {Folder}/rag.db: {Error}",
                        folderName, ex.Message);
                }
            }

            summaries.Add(new KbSummary(
                folderName,
                config.Name,
                totalFiles,
                totalChunks,
                isIndexing,
                schemaVersion,
                IsSchemaStale: schemaVersion < SqliteVectorStore.TargetUserVersion));
        }

        // Dispose cached instances for deleted KBs
        foreach (var cachedId in _instances.Keys)
        {
            if (!existingIds.Contains(cachedId) && _instances.TryRemove(cachedId, out var removed))
            {
                removed.Dispose();
            }
        }

        return summaries;
    }

    public void Dispose()
    {
        foreach (var instance in _instances.Values)
            instance.Dispose();

        _instances.Clear();
    }
}

/// <summary>
/// Holds the initialized services for a single knowledge base.
/// </summary>
public sealed class KbInstance : IDisposable
{
    public string KbId { get; }
    public string KbPath { get; }
    public RagConfig Config { get; }
    public SqliteVectorStore Store { get; }
    public HybridSearcher Searcher { get; }

    public KbInstance(
        string kbId,
        string kbPath,
        RagConfig config,
        SqliteVectorStore store,
        HybridSearcher searcher)
    {
        KbId = kbId;
        KbPath = kbPath;
        Config = config;
        Store = store;
        Searcher = searcher;
    }

    public void Dispose() => Store.Dispose();
}

/// <summary>
/// Lightweight summary of a knowledge base for listing.
/// </summary>
/// <param name="Id">Knowledge base identifier (matches folder name).</param>
/// <param name="Name">Human-readable name from config.json.</param>
/// <param name="TotalFiles">Number of indexed source files.</param>
/// <param name="TotalChunks">Number of indexed chunks.</param>
/// <param name="IsIndexing">Whether an indexing run is currently in progress.</param>
/// <param name="SchemaVersion">
/// Schema version this KB was last tagged with (<c>PRAGMA user_version</c>).
/// 0 means legacy — created or last indexed before v1.4.1, which is when
/// user_version tagging was introduced.
/// </param>
/// <param name="IsSchemaStale">
/// True when <see cref="SchemaVersion"/> is below
/// <see cref="Storage.SqliteVectorStore.TargetUserVersion"/>. Stale KBs still
/// serve search queries correctly; re-indexing triggers automatic migration
/// through the exec path.
/// </param>
public sealed record KbSummary(
    string Id,
    string Name,
    int TotalFiles,
    int TotalChunks,
    bool IsIndexing,
    int SchemaVersion,
    bool IsSchemaStale);
