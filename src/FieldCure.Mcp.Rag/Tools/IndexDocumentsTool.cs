using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FieldCure.DocumentParsers;
using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Contextualization;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Tools;

[McpServerToolType]
public static class IndexDocumentsTool
{
    const int MaxContextualizationParallelism = 4;

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
        IProgress<ProgressNotificationValue> progress,
        [Description("If true, re-indexes all files regardless of change detection.")]
        bool force = false,
        [Description("Custom system prompt for chunk contextualization. " +
                     "If provided, overrides the stored and default prompts. " +
                     "Set to null to use the stored prompt or built-in default.")]
        string? system_prompt = null,
        CancellationToken cancellationToken = default)
    {
        var store = context.Store;
        var embeddingProvider = context.EmbeddingProvider;
        var chunker = context.Chunker;
        var contextFolder = context.ContextFolder;

        // Acquire indexing lock
        if (!store.AcquireLock(Environment.ProcessId))
            return JsonSerializer.Serialize(
                new { error = "Another process is currently indexing this folder." }, JsonOptions);

        try
        {
        // Resolve effective system prompt: parameter > DB > env > built-in
        await ResolveAndApplyPromptAsync(context, store, system_prompt, force);

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

        // Safety limits
        const int softLimit = 1_000;
        const int hardLimit = 10_000;
        string? warning = null;

        if (files.Count > hardLimit)
        {
            store.ReleaseLock();
            return JsonSerializer.Serialize(new
            {
                error = $"Too many files ({files.Count:N0}). Hard limit is {hardLimit:N0}.",
                hint = "Specify a subfolder with fewer files to index.",
                found = files.Count,
                limit = hardLimit,
            }, JsonOptions);
        }

        if (files.Count > softLimit)
        {
            warning = $"{files.Count:N0} files found. Consider specifying a subfolder for faster indexing.";
        }

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

        var totalSw = Stopwatch.StartNew();
        var (tParse, tChunk, tContext, tEmbed, tStore) = (0L, 0L, 0L, 0L, 0L);
        var logPath = Path.Combine(context.DataRoot, "index_timing.log");
        using var logWriter = new StreamWriter(logPath, append: true);
        logWriter.AutoFlush = true;
        logWriter.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} force={force} files={files.Count} ---");

        var fileIndex = 0;
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

                var fileSw = Stopwatch.StartNew();
                var sw = Stopwatch.StartNew();

                // Parse document text
                var text = await ParseDocumentAsync(filePath);
                if (string.IsNullOrWhiteSpace(text))
                {
                    skipped++;
                    continue;
                }
                var parsedMs = sw.ElapsedMilliseconds;
                tParse += parsedMs;

                // Delete old chunks for this file
                await store.DeleteBySourcePathAsync(relativePath);

                // Chunk the text
                sw.Restart();
                var chunks = chunker.Split(text);
                if (chunks.Count == 0)
                {
                    skipped++;
                    continue;
                }
                var chunkedMs = sw.ElapsedMilliseconds;
                tChunk += chunkedMs;

                // Contextualize chunks (parallel for real contextualizers, sequential for NullChunkContextualizer)
                sw.Restart();
                var contextualizer = context.Contextualizer;
                var documentContext = ChunkContextualizerHelper.TruncateDocumentContext(text);
                var fileName = Path.GetFileName(filePath);

                var enrichedTexts = new string[chunks.Count];
                if (contextualizer is NullChunkContextualizer)
                {
                    for (var i = 0; i < chunks.Count; i++)
                        enrichedTexts[i] = chunks[i].Content;
                }
                else
                {
                    await Parallel.ForEachAsync(
                        Enumerable.Range(0, chunks.Count),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = MaxContextualizationParallelism,
                            CancellationToken = cancellationToken,
                        },
                        async (i, ct) =>
                        {
                            enrichedTexts[i] = await contextualizer.EnrichAsync(
                                chunks[i].Content,
                                documentContext,
                                fileName,
                                i,
                                chunks.Count,
                                ct);
                        });
                }
                var contextMs = sw.ElapsedMilliseconds;
                tContext += contextMs;

                // Batch embed using enriched text
                sw.Restart();
                var embeddings = await embeddingProvider.EmbedBatchAsync(
                    enrichedTexts, cancellationToken);
                var embedMs = sw.ElapsedMilliseconds;
                tEmbed += embedMs;

                // Upsert chunks with original content + enriched text
                sw.Restart();
                var pathHash = ComputeStringHash(relativePath);
                for (var i = 0; i < chunks.Count; i++)
                {
                    var chunk = new DocumentChunk
                    {
                        Id = $"{pathHash}_{i}",
                        SourcePath = relativePath,
                        ChunkIndex = i,
                        Content = chunks[i].Content,
                        CharOffset = chunks[i].CharOffset,
                    };
                    await store.UpsertChunkAsync(chunk, embeddings[i], embeddingProvider.ModelId, enrichedTexts[i]);
                }
                var storeMs = sw.ElapsedMilliseconds;
                tStore += storeMs;

                await store.SetFileHashAsync(relativePath, hash);
                indexed++;
                totalChunks += chunks.Count;

                var line = $"[Index] {fileName} — {chunks.Count} chunks, " +
                    $"parse={parsedMs}ms chunk={chunkedMs}ms context={contextMs}ms embed={embedMs}ms store={storeMs}ms " +
                    $"total={fileSw.ElapsedMilliseconds}ms";
                Console.Error.WriteLine(line);
                logWriter.WriteLine(line);
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{relativePath}: {ex.Message}");
            }
            finally
            {
                fileIndex++;
                store.UpdateProgress(fileIndex, files.Count);
                progress.Report(new ProgressNotificationValue
                {
                    Progress = fileIndex,
                    Total = files.Count,
                    Message = $"{Path.GetFileName(filePath)} ({fileIndex}/{files.Count})",
                });
            }
        }

        totalSw.Stop();
        var summary = $"[Index] Done — {indexed} files, {totalChunks} chunks in {totalSw.ElapsedMilliseconds}ms | " +
            $"parse={tParse}ms chunk={tChunk}ms context={tContext}ms embed={tEmbed}ms store={tStore}ms";
        Console.Error.WriteLine(summary);
        logWriter.WriteLine(summary);

        var result = new
        {
            indexed,
            skipped,
            failed,
            removed,
            total_chunks = totalChunks,
            errors,
            warning
        };

        return JsonSerializer.Serialize(result, JsonOptions);

        } // try
        finally
        {
            store.ReleaseLock();
        }
    }

    /// <summary>
    /// Resolves the effective system prompt using the priority chain:
    /// tool parameter > DB stored value > env var > built-in default.
    /// Updates the contextualizer's prompt and stores metadata in DB.
    /// </summary>
    static async Task ResolveAndApplyPromptAsync(
        RagContext context,
        SqliteVectorStore store,
        string? paramPrompt,
        bool force)
    {
        string? effectiveCustomPrompt;

        if (!string.IsNullOrWhiteSpace(paramPrompt))
        {
            // Explicit parameter: save to DB as user customization
            effectiveCustomPrompt = paramPrompt;
            await store.SetMetadataAsync(ChunkContextualizerHelper.MetaKeySystemPrompt, paramPrompt);
        }
        else
        {
            // No parameter: read from DB (null = use built-in default)
            effectiveCustomPrompt = await store.GetMetadataAsync(
                ChunkContextualizerHelper.MetaKeySystemPrompt);
        }

        // Apply to contextualizer: custom prompt or built-in default
        var effectivePrompt = effectiveCustomPrompt ?? ChunkContextualizerHelper.DefaultSystemPrompt;
        context.Contextualizer.SystemPrompt = effectivePrompt;

        // Store hash of the effective prompt for stale-index detection
        var hash = ChunkContextualizerHelper.ComputePromptHash(effectivePrompt);
        await store.SetMetadataAsync(ChunkContextualizerHelper.MetaKeyPromptHash, hash);
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
