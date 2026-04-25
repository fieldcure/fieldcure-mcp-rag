using System.ComponentModel;
using System.Net;
using System.Text.Json;
using FieldCure.Mcp.Rag.Configuration;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Search;
using FieldCure.Mcp.Rag.Services;
using FieldCure.Mcp.Rag.Storage;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that performs hybrid document search across a selected knowledge base.
/// </summary>
[McpServerToolType]
public static class SearchDocumentsTool
{
    /// <summary>
    /// Searches a knowledge base using BM25, vector, or hybrid ranking depending
    /// on provider availability and the caller's requested mode.
    /// </summary>
    /// <param name="server">The active MCP server instance.</param>
    /// <param name="context">The shared multi-knowledge-base context.</param>
    /// <param name="keyResolvers">Lazy API key resolver registry for embedding providers.</param>
    /// <param name="kb_id">Knowledge base identifier.</param>
    /// <param name="query">Natural-language search query.</param>
    /// <param name="top_k">Maximum number of results to return.</param>
    /// <param name="threshold">Minimum vector similarity score.</param>
    /// <param name="search_mode">Requested search mode override.</param>
    /// <param name="cancellationToken">Cancellation token for the search operation.</param>
    /// <returns>A JSON payload containing ranked results or an error object.</returns>
    [McpServerTool(Name = "search_documents", ReadOnly = true, Destructive = false, Idempotent = true), Description(
        "Searches documents in a knowledge base using hybrid BM25 keyword + vector semantic search. " +
        "Returns ranked results with source file and content preview.")]
    public static async Task<string> SearchDocuments(
        McpServer server,
        MultiKbContext context,
        ApiKeyResolverRegistry keyResolvers,
        [Description("Knowledge base ID")]
        string kb_id,
        [Description("Natural language search query")]
        string query,
        [Description("Maximum number of results (default: 5)")]
        int top_k = 5,
        [Description("Minimum similarity score 0-1 (default: 0.3)")]
        float threshold = 0.3f,
        [Description(
            "Search strategy (optional, default 'auto'): " +
            "'auto' = hybrid when embedder is available, else BM25 (recommended); " +
            "'bm25' = keyword-only, use only when the user explicitly asks for keyword/exact match " +
            "or when vector search is known to be unavailable; " +
            "'vector' = embedding-only, use only when the user explicitly asks for semantic/meaning-based search. " +
            "Do not override the default to optimize for speed or cost — the server already handles fallback correctly.")]
        string search_mode = "auto",
        CancellationToken cancellationToken = default)
    {
        var requestedMode = search_mode?.ToLowerInvariant() switch
        {
            "bm25" => SearchMode.Bm25Only,
            "vector" => SearchMode.VectorOnly,
            _ => (SearchMode?)null,
        };

        var kbPath = Path.Combine(context.BasePath, kb_id);
        if (!Directory.Exists(kbPath))
            return JsonSerializer.Serialize(new { error = $"Knowledge base not found: {kb_id}" }, McpJson.Search);

        var dbPath = Path.Combine(kbPath, "rag.db");
        if (!File.Exists(dbPath))
            return JsonSerializer.Serialize(new { error = $"Database not found for knowledge base: {kb_id}" }, McpJson.Search);

        var config = RagConfig.Load(kbPath);

        using var store = new SqliteVectorStore(dbPath, readOnly: true);

        var firstAttempt = await CreateEmbeddingProviderAsync(server, keyResolvers, config.Embedding, requestedMode, cancellationToken);
        if (firstAttempt.error is not null)
            return JsonSerializer.Serialize(new { error = firstAttempt.error }, McpJson.Search);

        var firstResult = await TrySearchAsync(store, firstAttempt.provider!, query, top_k, threshold, requestedMode, cancellationToken);
        if (firstResult.retryableAuthFailure && firstAttempt.envVarName is not null)
        {
            keyResolvers.Invalidate(firstAttempt.envVarName);
            var retryProvider = await CreateEmbeddingProviderAsync(server, keyResolvers, config.Embedding, requestedMode, cancellationToken, forceInteractive: true);
            if (retryProvider.error is not null)
                return JsonSerializer.Serialize(new { error = retryProvider.error }, McpJson.Search);

            firstResult = await TrySearchAsync(store, retryProvider.provider!, query, top_k, threshold, requestedMode, cancellationToken);
        }

        if (firstResult.error is not null)
            return JsonSerializer.Serialize(new { error = firstResult.error }, McpJson.Search);

        var hybrid = firstResult.result!;

        var response = new
        {
            kb_id,
            results = hybrid.Results.Select(r => new
            {
                chunk_id = r.ChunkId,
                source_path = r.SourcePath,
                chunk_index = r.ChunkIndex,
                total_chunks = r.TotalChunks,
                content = r.Content,
                score = r.Score,
                has_previous = r.ChunkIndex > 0,
                has_next = r.TotalChunks > 0 && r.ChunkIndex < r.TotalChunks - 1,
            }),
            query,
            search_mode = hybrid.Mode,
            total_chunks_searched = hybrid.TotalChunksSearched,
        };

        return JsonSerializer.Serialize(response, McpJson.Search);
    }

    /// <summary>
    /// Creates the embedding provider for a search request, eliciting API keys
    /// lazily when needed and falling back to a null provider when allowed.
    /// </summary>
    static async Task<(IEmbeddingProvider? provider, string? envVarName, string? error)> CreateEmbeddingProviderAsync(
        McpServer server,
        ApiKeyResolverRegistry keyResolvers,
        ProviderConfig config,
        SearchMode? requestedMode,
        CancellationToken ct,
        bool forceInteractive = false)
    {
        if (string.IsNullOrWhiteSpace(config.Model))
            return (new NullEmbeddingProvider(), null, null);

        // Local providers need no key — build immediately.
        if (config.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            return (EmbeddingProviderFactory.Create(config, apiKey: ""), null, null);

        var envVarName = ApiKeyEnvironment.GetEnvVarName(config.ApiKeyPreset);
        if (envVarName is null)
            return (EmbeddingProviderFactory.Create(config, apiKey: ""), null, null);

        if (forceInteractive)
            keyResolvers.Invalidate(envVarName);

        var apiKey = await keyResolvers.ResolveAsync(server, envVarName, config.ApiKeyPreset ?? config.Provider, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (requestedMode == SearchMode.VectorOnly)
                return (null, envVarName, keyResolvers.BuildSoftFailMessage(envVarName));

            return (new NullEmbeddingProvider(), envVarName, null);
        }

        return (EmbeddingProviderFactory.Create(config, apiKey), envVarName, null);
    }

    /// <summary>
    /// Executes one hybrid search attempt and classifies authentication failures
    /// so the caller can invalidate the key and retry interactively.
    /// </summary>
    static async Task<(HybridSearchResult? result, string? error, bool retryableAuthFailure)> TrySearchAsync(
        SqliteVectorStore store,
        IEmbeddingProvider provider,
        string query,
        int topK,
        float threshold,
        SearchMode? requestedMode,
        CancellationToken ct)
    {
        try
        {
            var searcher = new HybridSearcher(store, provider);
            var result = await searcher.SearchAsync(query, topK, threshold, requestedMode, ct);
            return (result, null, false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (null, null, true);
        }
        catch (Exception ex)
        {
            return (null, ex.Message, false);
        }
    }

}
