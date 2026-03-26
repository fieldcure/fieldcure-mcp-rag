using System.ComponentModel;
using System.Text.Json;
using FieldCure.Mcp.Rag.Contextualization;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

/// <summary>
/// MCP tool that returns index metadata for the host application.
/// Annotated with ReadOnlyHint and UserInteractionRequired so that
/// AI models do not call this tool autonomously.
/// </summary>
[McpServerToolType]
public static class GetIndexInfoTool
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "get_index_info"),
     Description(
        "Internal tool for host application. Returns index metadata including " +
        "file/chunk counts, system prompt configuration, and prompt hash for " +
        "stale-index detection. Do not call unless explicitly requested by the user.")]
    public static async Task<string> GetIndexInfo(
        RagContext context,
        CancellationToken cancellationToken = default)
    {
        var store = context.Store;

        // Counts
        var totalChunks = await store.GetTotalChunkCountAsync();
        var indexedPaths = await store.GetIndexedPathsAsync();

        // Metadata
        var storedPrompt = await store.GetMetadataAsync(
            ChunkContextualizerHelper.MetaKeySystemPrompt);
        var storedHash = await store.GetMetadataAsync(
            ChunkContextualizerHelper.MetaKeyPromptHash);

        // Current built-in default hash for comparison
        var defaultPromptHash = ChunkContextualizerHelper.ComputePromptHash(
            ChunkContextualizerHelper.DefaultSystemPrompt);

        var result = new
        {
            folder = context.ContextFolder,
            total_files = indexedPaths.Count,
            total_chunks = totalChunks,
            system_prompt = storedPrompt,          // null = using built-in default
            effective_prompt_hash = storedHash,     // hash of prompt used during last indexing
            default_prompt = ChunkContextualizerHelper.DefaultSystemPrompt,
            default_prompt_hash = defaultPromptHash,
            is_prompt_stale = storedHash is not null && storedHash != defaultPromptHash && storedPrompt is null,
            contextualizer = context.Contextualizer.GetType().Name,
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
