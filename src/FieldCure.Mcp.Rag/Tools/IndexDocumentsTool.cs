using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using FieldCure.DocumentParsers;
using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Storage;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

[McpServerToolType]
public static class IndexDocumentsTool
{
    static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md"
    };

    /// <summary>
    /// Returns all supported file extensions: DocumentParsers formats + plain text.
    /// Automatically updated when new parsers are registered.
    /// </summary>
    static readonly HashSet<string> SupportedExtensions =
        new(DocumentParserFactory.SupportedExtensions.Concat(PlainTextExtensions), StringComparer.OrdinalIgnoreCase);

    [McpServerTool(Name = "index_documents"), Description(
        "Indexes all supported documents in the context folder into the vector store. " +
        "Performs incremental updates — only changed or new files are re-indexed. " +
        "Supported formats: DOCX, HWPX, TXT, MD (auto-extends when new parsers are added).")]
    public static async Task<string> IndexDocuments(
        RagContext context,
        [Description("If true, re-indexes all files regardless of change detection.")]
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var store = context.Store;
        var embeddingProvider = context.EmbeddingProvider;
        var chunker = context.Chunker;
        var contextFolder = context.ContextFolder;

        var indexed = 0;
        var skipped = 0;
        var failed = 0;
        var removed = 0;
        var totalChunks = 0;
        var errors = new List<string>();

        var files = Directory.EnumerateFiles(contextFolder, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(contextFolder, f);
                // Exclude .rag folder
                return !rel.StartsWith(".rag", StringComparison.OrdinalIgnoreCase)
                       && SupportedExtensions.Contains(Path.GetExtension(f));
            })
            .ToList();

        // Orphan cleanup: remove DB entries for files that no longer exist on disk
        var actualPaths = files
            .Select(f => Path.GetRelativePath(contextFolder, f).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var indexedPaths = await store.GetIndexedPathsAsync();
        var orphanPaths = indexedPaths.Where(p => !actualPaths.Contains(p)).ToList();

        foreach (var orphan in orphanPaths)
        {
            await store.PurgeSourcePathAsync(orphan);
            removed++;
        }

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(contextFolder, filePath)
                .Replace('\\', '/');

            try
            {
                var hash = await ComputeFileHashAsync(filePath);

                if (!force)
                {
                    var storedHash = await store.GetFileHashAsync(relativePath);
                    if (storedHash == hash)
                    {
                        skipped++;
                        continue;
                    }
                }

                // Parse document text
                var text = await ParseDocumentAsync(filePath);
                if (string.IsNullOrWhiteSpace(text))
                {
                    skipped++;
                    continue;
                }

                // Delete old chunks for this file
                await store.DeleteBySourcePathAsync(relativePath);

                // Chunk the text
                var chunks = chunker.Split(text);
                if (chunks.Count == 0)
                {
                    skipped++;
                    continue;
                }

                // Batch embed
                var texts = chunks.Select(c => c.Content).ToList();
                var embeddings = await embeddingProvider.EmbedBatchAsync(texts, cancellationToken);

                // Upsert chunks
                var pathHash = ComputeStringHash(relativePath);
                for (int i = 0; i < chunks.Count; i++)
                {
                    var chunk = new DocumentChunk
                    {
                        Id = $"{pathHash}_{i}",
                        SourcePath = relativePath,
                        ChunkIndex = i,
                        Content = chunks[i].Content,
                        CharOffset = chunks[i].CharOffset,
                    };
                    await store.UpsertChunkAsync(chunk, embeddings[i], embeddingProvider.ModelId);
                }

                await store.SetFileHashAsync(relativePath, hash);
                indexed++;
                totalChunks += chunks.Count;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{relativePath}: {ex.Message}");
            }
        }

        var result = new
        {
            indexed,
            skipped,
            failed,
            removed,
            total_chunks = totalChunks,
            errors
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    static async Task<string> ParseDocumentAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension is ".txt" or ".md")
            return await File.ReadAllTextAsync(filePath);

        // Use FieldCure.DocumentParsers for supported document formats
        var parser = DocumentParserFactory.GetParser(extension);
        if (parser is not null)
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            return parser.ExtractText(bytes);
        }

        return "";
    }

    static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static string ComputeStringHash(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
