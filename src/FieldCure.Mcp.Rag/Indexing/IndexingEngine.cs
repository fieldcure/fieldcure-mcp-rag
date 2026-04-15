using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>All file extensions supported by the indexing engine.</summary>
    internal static readonly HashSet<string> SupportedExtensions =
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
    /// Runs the full indexing pipeline with stage-level failure handling.
    /// </summary>
    public async Task<IndexingResult> RunAsync(bool force, CancellationToken cancellationToken)
    {
        // Acquire lock
        if (!_store.AcquireLock(Environment.ProcessId))
        {
            _logger.LogError("Another process is currently indexing this knowledge base.");
            return new IndexingResult
            {
                Indexed = 0, Skipped = 0, Failed = 0, Degraded = 0, PartiallyDeferred = 0,
                FailedFiles = [], Duration = TimeSpan.Zero, ExitCode = 1,
            };
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
                return new IndexingResult
                {
                    Indexed = 0, Skipped = 0, Failed = 0, Degraded = 0, PartiallyDeferred = 0,
                    FailedFiles = [], Duration = TimeSpan.Zero, ExitCode = 0,
                };
            }

            _logger.LogInformation("Found {Count} files to process.", files.Count);

            // Orphan cleanup
            var removed = await CleanOrphansAsync(files);
            if (removed > 0)
                _logger.LogInformation("Removed {Count} orphaned file entries.", removed);

            // Process files
            var (indexed, skipped, failed, degraded, partiallyDeferred, totalChunks) = (0, 0, 0, 0, 0, 0);
            var failedFiles = new List<FailedFile>();
            var providerHealth = ProviderHealth.Ok;
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

                    totalSw.Stop();
                    await PersistMetadataAsync(indexed, skipped, failed, degraded, partiallyDeferred,
                        failedFiles, totalSw.Elapsed, providerHealth);

                    return new IndexingResult
                    {
                        Indexed = indexed, Skipped = skipped, Failed = failed,
                        Degraded = degraded, PartiallyDeferred = partiallyDeferred,
                        FailedFiles = failedFiles, Duration = totalSw.Elapsed, ExitCode = 2,
                    };
                }

                cancellationToken.ThrowIfCancellationRequested();

                var storagePath = ComputeStoragePath(filePath, sourcePath);

                try
                {
                    var hash = await ComputeFileHashAsync(filePath);

                    if (!force)
                    {
                        var storedHash = await _store.GetFileHashAsync(storagePath);
                        if (storedHash == hash)
                        {
                            skipped++;
                            continue;
                        }
                    }

                    var fileSw = Stopwatch.StartNew();

                    // === Stage 1: Extract ===
                    string text;
                    try
                    {
                        text = await ParseDocumentAsync(filePath);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        throw new FileExtractionException(filePath, ex.Message, ex);
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        skipped++;
                        continue;
                    }

                    // === Stage 2: Chunk ===
                    var chunks = _chunker.Split(text);
                    if (chunks.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    // === Stage 3: Contextualize (per-chunk, parallel) ===
                    var enrichResults = await ContextualizeChunksAsync(
                        chunks, text, filePath, cancellationToken);

                    var anyContextualizationFailed = enrichResults.Any(r => !r.IsContextualized);
                    if (anyContextualizationFailed && providerHealth == ProviderHealth.Ok)
                        providerHealth = ProviderHealth.ContextualizerUnavailable;

                    // === Stage 4: Embed (batch) ===
                    float[][] embeddings;
                    try
                    {
                        _store.UpdateProgress(fileIndex, files.Count, "embedding",
                            failed, providerHealth);

                        var textsToEmbed = enrichResults.Select(r => r.Text).ToArray();
                        embeddings = await _embeddingProvider.EmbedBatchAsync(textsToEmbed, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (HttpRequestException httpEx)
                    {
                        // Embedding API returned a non-success status (or transport failed).
                        // Preserve the status code so the caller can classify the failure
                        // (401/403 abort vs 429/5xx retry vs 400 needs caller intervention).
                        throw new EmbeddingException(
                            filePath, httpEx.Message, httpEx, statusCode: httpEx.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        throw new EmbeddingException(filePath, ex.Message, ex);
                    }

                    // === Stage 5: Persist (atomic) ===
                    var rawCount = enrichResults.Count(r => !r.IsContextualized);
                    var fileStatus = rawCount > 0 ? FileIndexStatus.Degraded : FileIndexStatus.Ready;

                    var pathHash = ComputeStringHash(storagePath);
                    var docChunks = new DocumentChunk[chunks.Count];
                    var chunkInfos = new ChunkWriteInfo[chunks.Count];
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        docChunks[i] = new DocumentChunk
                        {
                            Id = $"{pathHash}_{i}",
                            SourcePath = storagePath,
                            ChunkIndex = i,
                            Content = chunks[i].Content,
                            CharOffset = chunks[i].CharOffset,
                        };
                        chunkInfos[i] = new ChunkWriteInfo
                        {
                            EnrichedText = enrichResults[i].Text,
                            Status = ChunkIndexStatus.Indexed,
                            IsContextualized = enrichResults[i].IsContextualized,
                            LastError = enrichResults[i].FailureReason,
                        };
                    }

                    var fileInfo = new FileWriteInfo
                    {
                        FileHash = hash,
                        Status = fileStatus,
                        ChunksRaw = rawCount,
                        ChunksPending = 0,
                    };

                    await _store.ReplaceFileChunksAsync(
                        storagePath, docChunks, embeddings, _embeddingProvider.ModelId,
                        chunkInfos, fileInfo);

                    if (fileStatus == FileIndexStatus.Degraded) degraded++;
                    indexed++;
                    totalChunks += chunks.Count;

                    var line = $"[Index] {Path.GetFileName(filePath)} — {chunks.Count} chunks" +
                               (rawCount > 0 ? $" ({rawCount} raw)" : "") +
                               $", total={fileSw.ElapsedMilliseconds}ms";
                    _logger.LogInformation("{Line}", line);
                    logWriter.WriteLine(line);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (FileExtractionException ex)
                {
                    // Stage 1 failure — for previously indexed files, update file_index
                    // to NeedsAction while preserving existing chunks. For a first-time
                    // file that fails extraction, MarkFileAsFailedAsync returns false
                    // (no existing row to UPDATE) and the file will appear as "added" in
                    // the next check_changes call. We surface that case with a warning
                    // so operators can spot the stuck-new-file pattern in the logs.
                    var updated = await _store.MarkFileAsFailedAsync(
                        storagePath, FileIndexStatus.NeedsAction, ex.Message, "extract");
                    if (!updated)
                    {
                        _logger.LogWarning(
                            "[Indexing] Extract failed for {File} but no file_index row exists; " +
                            "file will surface as 'added' on the next check_changes.", storagePath);
                    }
                    failed++;
                    failedFiles.Add(new FailedFile(storagePath,
                        $"{ex.InnerException?.GetType().Name ?? "Exception"}: {ex.Message}"));
                    _logger.LogWarning(ex,
                        "[Indexing] Extract failed for {File}, previous chunks preserved", storagePath);
                    logWriter.WriteLine($"[FAILED:extract] {storagePath} — {ex.Message}");
                }
                catch (EmbeddingException ex)
                {
                    // Stage 4 failure — preserve existing chunks, mark as deferred
                    await _store.MarkFileAsFailedAsync(
                        storagePath, FileIndexStatus.PartiallyDeferred, ex.Message, "embed");
                    partiallyDeferred++;
                    providerHealth = ProviderHealth.EmbeddingUnavailable;
                    _logger.LogWarning(ex,
                        "[Indexing] Embedding failed for {File}, deferred to next cycle", storagePath);
                    logWriter.WriteLine($"[DEFERRED:embed] {storagePath} — {ex.Message}");
                }
                catch (Exception ex)
                {
                    // Unexpected failure — do not touch file_index
                    failed++;
                    failedFiles.Add(new FailedFile(storagePath, $"{ex.GetType().Name}: {ex.Message}"));
                    _logger.LogError(ex, "[Indexing] Unexpected failure for {File}", storagePath);
                    logWriter.WriteLine($"[FAILED] {storagePath} — {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    fileIndex++;
                    _store.UpdateProgress(fileIndex, files.Count,
                        currentStage: null, failedCount: failed, providerHealth: providerHealth);
                }
            }

            totalSw.Stop();
            var duration = totalSw.Elapsed;

            var summary = $"[Index] Done — indexed={indexed} skipped={skipped} failed={failed} " +
                          $"degraded={degraded} deferred={partiallyDeferred} " +
                          $"removed={removed} chunks={totalChunks} elapsed={totalSw.ElapsedMilliseconds}ms";
            _logger.LogInformation("{Summary}", summary);
            logWriter.WriteLine(summary);

            await PersistMetadataAsync(indexed, skipped, failed, degraded, partiallyDeferred,
                failedFiles, duration, providerHealth);

            var exitCode = failed > 0 && indexed == 0 ? 1 : 0;
            return new IndexingResult
            {
                Indexed = indexed, Skipped = skipped, Failed = failed,
                Degraded = degraded, PartiallyDeferred = partiallyDeferred,
                FailedFiles = failedFiles, Duration = duration, ExitCode = exitCode,
            };
        }
        finally
        {
            _store.ReleaseLock();
        }
    }

    #region Stage Methods

    /// <summary>
    /// Stage 3: Contextualize all chunks in parallel.
    /// Returns <see cref="EnrichResult"/> per chunk — failures are non-fatal.
    /// </summary>
    async Task<EnrichResult[]> ContextualizeChunksAsync(
        IReadOnlyList<(string Content, int CharOffset)> chunks,
        string documentText,
        string filePath,
        CancellationToken ct)
    {
        var results = new EnrichResult[chunks.Count];
        var documentContext = ChunkContextualizerHelper.TruncateDocumentContext(documentText);
        var fileName = Path.GetFileName(filePath);

        if (_contextualizer is NullChunkContextualizer)
        {
            for (var i = 0; i < chunks.Count; i++)
                results[i] = EnrichResult.Success(chunks[i].Content);
        }
        else
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunks.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxContextualizationParallelism,
                    CancellationToken = ct,
                },
                async (i, innerCt) =>
                {
                    results[i] = await _contextualizer.EnrichAsync(
                        chunks[i].Content, documentContext, fileName, i, chunks.Count, innerCt);
                });
        }

        return results;
    }

    #endregion

    #region Metadata

    /// <summary>
    /// Persists all post-run metadata to index_metadata table.
    /// </summary>
    async Task PersistMetadataAsync(
        int indexed, int skipped, int failed, int degraded, int partiallyDeferred,
        List<FailedFile> failedFiles, TimeSpan duration, ProviderHealth providerHealth)
    {
        // Sanity check: the in-memory counters should match the actual DB state
        // after a successful 2-commit run. A mismatch is a bug — either the
        // counter was incremented without a corresponding persist (the classic
        // v1.4.0 MarkFileAsFailedAsync no-op bug) or the DB was mutated outside
        // the normal code path. Warn loudly with both numbers so it's easy to
        // triage from the logs.
        try
        {
            var dbDegraded = await _store.CountFilesByStatusAsync(Models.FileIndexStatus.Degraded);
            if (dbDegraded != degraded)
            {
                _logger.LogWarning(
                    "Counter mismatch: in-memory degraded={Counter}, DB Degraded count={DbCount}",
                    degraded, dbDegraded);
            }

            var dbDeferred = await _store.CountFilesByStatusAsync(Models.FileIndexStatus.PartiallyDeferred);
            if (dbDeferred != partiallyDeferred)
            {
                _logger.LogWarning(
                    "Counter mismatch: in-memory partiallyDeferred={Counter}, DB PartiallyDeferred count={DbCount}",
                    partiallyDeferred, dbDeferred);
            }
        }
        catch (Exception ex)
        {
            // Sanity check is diagnostic only — never fail metadata persistence because of it.
            _logger.LogDebug(ex, "Counter sanity check failed (non-fatal)");
        }

        // Legacy keys (backward compatible)
        await _store.SetMetadataAsync("last_failed_count", failed.ToString());
        await _store.SetMetadataAsync("last_failed_files",
            JsonSerializer.Serialize(failedFiles.Select(f => f.Path)));
        await _store.SetMetadataAsync("last_failed_reasons",
            JsonSerializer.Serialize(failedFiles.Select(f => f.Reason)));

        // v1.4 keys
        await _store.SetMetadataAsync("last_indexed_count", indexed.ToString());
        await _store.SetMetadataAsync("last_skipped_count", skipped.ToString());
        await _store.SetMetadataAsync("last_degraded_count", degraded.ToString());
        await _store.SetMetadataAsync("last_partially_deferred_count", partiallyDeferred.ToString());
        await _store.SetMetadataAsync("last_run_duration_ms", ((int)duration.TotalMilliseconds).ToString());
        await _store.SetMetadataAsync("last_run_completed_utc", DateTimeOffset.UtcNow.ToString("O"));
        await _store.SetMetadataAsync("last_provider_health", ((int)providerHealth).ToString());
    }

    #endregion

    #region File Collection & Helpers

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
            actualPaths.Add(ComputeStoragePath(filePath, sourcePath));

        var indexedPaths = await _store.GetIndexedPathsAsync();
        var orphans = indexedPaths.Where(p => !actualPaths.Contains(p)).ToList();

        foreach (var orphan in orphans)
            await _store.PurgeSourcePathAsync(orphan);

        return orphans.Count;
    }

    /// <summary>Computes the storage-relative path for a file.</summary>
    string ComputeStoragePath(string filePath, string sourcePath)
    {
        var relativePath = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');
        var sourceIndex = _config.SourcePaths.IndexOf(sourcePath);
        return _config.SourcePaths.Count > 1 ? $"{sourceIndex}/{relativePath}" : relativePath;
    }

    /// <summary>Checks whether a cancel file exists in the KB directory.</summary>
    bool IsCancelled() => File.Exists(Path.Combine(_kbPath, "cancel"));

    /// <summary>Deletes the cancel file if it exists.</summary>
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

    /// <summary>Computes a SHA-256 hash of the file contents for change detection.</summary>
    static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Computes a truncated SHA-256 hash (first 16 hex chars) for a string value.</summary>
    static string ComputeStringHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    #endregion
}
