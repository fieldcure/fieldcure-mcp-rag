using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FieldCure.DocumentParsers;
using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Configuration;
using FieldCure.Mcp.Rag.Contextualization;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Storage;
using Microsoft.Extensions.Logging;

namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Headless indexing engine for exec mode.
/// Scans source paths, detects changes, chunks, contextualizes, embeds, and stores.
/// Supports cancel file for graceful shutdown and resumable processing.
/// </summary>
public sealed class IndexingEngine
{
    const int MaxContextualizationParallelism = 4;

    static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md"
    };

    static readonly HashSet<string> SupportedExtensions =
        new(DocumentParserFactory.SupportedExtensions.Concat(PlainTextExtensions), StringComparer.OrdinalIgnoreCase);

    readonly string _kbPath;
    readonly RagConfig _config;
    readonly SqliteVectorStore _store;
    readonly IEmbeddingProvider _embeddingProvider;
    readonly TextChunker _chunker;
    readonly IChunkContextualizer _contextualizer;
    readonly ILogger _logger;

    public IndexingEngine(
        string kbPath,
        RagConfig config,
        SqliteVectorStore store,
        IEmbeddingProvider embeddingProvider,
        TextChunker chunker,
        IChunkContextualizer contextualizer,
        ILogger logger)
    {
        _kbPath = kbPath;
        _config = config;
        _store = store;
        _embeddingProvider = embeddingProvider;
        _chunker = chunker;
        _contextualizer = contextualizer;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full indexing pipeline.
    /// Returns 0 on success, 1 on failure, 2 on cancellation.
    /// </summary>
    public async Task<int> RunAsync(bool force, CancellationToken cancellationToken)
    {
        // Acquire lock
        if (!_store.AcquireLock(Environment.ProcessId))
        {
            _logger.LogError("Another process is currently indexing this knowledge base.");
            return 1;
        }

        try
        {
            // Apply system prompt
            var effectivePrompt = _config.SystemPrompt ?? ChunkContextualizerHelper.DefaultSystemPrompt;
            _contextualizer.SystemPrompt = effectivePrompt;
            await _store.SetMetadataAsync(
                ChunkContextualizerHelper.MetaKeyPromptHash,
                ChunkContextualizerHelper.ComputePromptHash(effectivePrompt));

            if (!string.IsNullOrWhiteSpace(_config.SystemPrompt))
                await _store.SetMetadataAsync(
                    ChunkContextualizerHelper.MetaKeySystemPrompt, _config.SystemPrompt);

            // Collect files from all source paths
            var files = CollectFiles();
            if (files.Count == 0)
            {
                _logger.LogWarning("No supported files found in source paths.");
                return 0;
            }

            _logger.LogInformation("Found {Count} files to process.", files.Count);

            // Orphan cleanup
            var removed = await CleanOrphansAsync(files);
            if (removed > 0)
                _logger.LogInformation("Removed {Count} orphaned file entries.", removed);

            // Process files
            var (indexed, skipped, failed, totalChunks) = (0, 0, 0, 0);
            var totalSw = Stopwatch.StartNew();
            var logPath = Path.Combine(_kbPath, "index_timing.log");
            using var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
            logWriter.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} force={force} files={files.Count} ---");

            var fileIndex = 0;
            foreach (var (filePath, sourcePath) in files)
            {
                // Check cancel file
                if (IsCancelled())
                {
                    _logger.LogInformation("Cancel file detected. Stopping after {Count} files.", fileIndex);
                    _store.ReleaseLock();
                    CleanCancelFile();
                    return 2;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');
                // Prefix with source path index for uniqueness across multiple source paths
                var sourceIndex = _config.SourcePaths.IndexOf(sourcePath);
                var storagePath = _config.SourcePaths.Count > 1
                    ? $"{sourceIndex}/{relativePath}"
                    : relativePath;

                try
                {
                    var hash = await ComputeFileHashAsync(filePath);

                    if (!force)
                    {
                        var storedHash = await _store.GetFileHashAsync(storagePath);
                        if (storedHash == hash)
                        {
                            skipped++;
                            fileIndex++;
                            _store.UpdateProgress(fileIndex, files.Count);
                            continue;
                        }
                    }

                    var fileSw = Stopwatch.StartNew();

                    // Parse
                    var text = await ParseDocumentAsync(filePath);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        skipped++;
                        fileIndex++;
                        _store.UpdateProgress(fileIndex, files.Count);
                        continue;
                    }

                    // Delete old chunks
                    await _store.DeleteBySourcePathAsync(storagePath);

                    // Chunk
                    var chunks = _chunker.Split(text);
                    if (chunks.Count == 0)
                    {
                        skipped++;
                        fileIndex++;
                        _store.UpdateProgress(fileIndex, files.Count);
                        continue;
                    }

                    // Contextualize
                    var documentContext = ChunkContextualizerHelper.TruncateDocumentContext(text);
                    var fileName = Path.GetFileName(filePath);
                    var enrichedTexts = new string[chunks.Count];

                    if (_contextualizer is NullChunkContextualizer)
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
                                enrichedTexts[i] = await _contextualizer.EnrichAsync(
                                    chunks[i].Content, documentContext, fileName, i, chunks.Count, ct);
                            });
                    }

                    // Embed
                    var embeddings = await _embeddingProvider.EmbedBatchAsync(enrichedTexts, cancellationToken);

                    // Store
                    var pathHash = ComputeStringHash(storagePath);
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var chunk = new DocumentChunk
                        {
                            Id = $"{pathHash}_{i}",
                            SourcePath = storagePath,
                            ChunkIndex = i,
                            Content = chunks[i].Content,
                            CharOffset = chunks[i].CharOffset,
                        };
                        await _store.UpsertChunkAsync(chunk, embeddings[i], _embeddingProvider.ModelId, enrichedTexts[i]);
                    }

                    await _store.SetFileHashAsync(storagePath, hash);
                    indexed++;
                    totalChunks += chunks.Count;

                    var line = $"[Index] {fileName} — {chunks.Count} chunks, total={fileSw.ElapsedMilliseconds}ms";
                    _logger.LogInformation("{Line}", line);
                    logWriter.WriteLine(line);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Failed to index {Path}", storagePath);
                }
                finally
                {
                    fileIndex++;
                    _store.UpdateProgress(fileIndex, files.Count);
                }
            }

            totalSw.Stop();
            var summary = $"[Index] Done — indexed={indexed} skipped={skipped} failed={failed} " +
                          $"removed={removed} chunks={totalChunks} elapsed={totalSw.ElapsedMilliseconds}ms";
            _logger.LogInformation("{Summary}", summary);
            logWriter.WriteLine(summary);

            return failed > 0 && indexed == 0 ? 1 : 0;
        }
        finally
        {
            _store.ReleaseLock();
        }
    }

    /// <summary>Collects all supported files from configured source paths.</summary>
    List<(string FilePath, string SourcePath)> CollectFiles()
    {
        var files = new List<(string, string)>();

        foreach (var sourcePath in _config.SourcePaths)
        {
            if (!Directory.Exists(sourcePath))
            {
                _logger.LogWarning("Source path does not exist: {Path}", sourcePath);
                continue;
            }

            var found = Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));

            foreach (var f in found)
                files.Add((f, sourcePath));
        }

        return files;
    }

    /// <summary>Removes DB entries for files that no longer exist on disk.</summary>
    async Task<int> CleanOrphansAsync(List<(string FilePath, string SourcePath)> files)
    {
        var actualPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (filePath, sourcePath) in files)
        {
            var rel = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');
            var sourceIndex = _config.SourcePaths.IndexOf(sourcePath);
            var storagePath = _config.SourcePaths.Count > 1 ? $"{sourceIndex}/{rel}" : rel;
            actualPaths.Add(storagePath);
        }

        var indexedPaths = await _store.GetIndexedPathsAsync();
        var orphans = indexedPaths.Where(p => !actualPaths.Contains(p)).ToList();

        foreach (var orphan in orphans)
            await _store.PurgeSourcePathAsync(orphan);

        return orphans.Count;
    }

    bool IsCancelled() => File.Exists(Path.Combine(_kbPath, "cancel"));

    void CleanCancelFile()
    {
        var cancelPath = Path.Combine(_kbPath, "cancel");
        if (File.Exists(cancelPath))
            File.Delete(cancelPath);
    }

    /// <summary>
    /// Extracts text content from a file using DocumentParsers, falling back to raw read for .txt/.md.
    /// </summary>
    static async Task<string> ParseDocumentAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension is ".txt" or ".md")
            return await File.ReadAllTextAsync(filePath);

        var parser = DocumentParserFactory.GetParser(extension);
        if (parser is not null)
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            return parser.ExtractText(bytes);
        }

        return "";
    }

    /// <summary>
    /// Computes a SHA-256 hash of the file contents for change detection.
    /// </summary>
    static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes a truncated SHA-256 hash (first 16 hex chars) for a string value.
    /// </summary>
    static string ComputeStringHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
