using System.Diagnostics;
using System.Net;
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

    /// <summary>
    /// Per-chunk retry ceiling for the deferred-retry second pass. Once a
    /// chunk has been attempted this many times (across separate exec runs),
    /// it is marked <see cref="Models.ChunkIndexStatus.Failed"/> and stops
    /// consuming provider calls.
    /// </summary>
    public const int MaxEmbeddingRetries = 3;

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
            var (indexed, skipped, failed, degraded, partiallyDeferred, needsAction, totalChunks) =
                (0, 0, 0, 0, 0, 0, 0);
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
                        NeedsAction = needsAction,
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
                        // Hash-skip with v1.4.2 status awareness. Skip whenever the
                        // file_index row exists, hash matches, AND the status tells
                        // us the file should not re-enter the main loop — the
                        // decision lives in FileIndexStatusExtensions so every
                        // status is handled in one place.
                        //
                        // NeedsAction is skipped but reported through a dedicated
                        // counter so operators can see how many files are stuck on
                        // an extraction-stage structural problem. Failed uses the
                        // plain skipped counter because that bucket already means
                        // "waiting for user action" (retry exhausted).
                        //
                        // The Principle 5 guard lives here too: PartiallyDeferred
                        // files must never re-enter the main loop because Commit 1
                        // has already persisted their OCR/contextualization output
                        // and the deferred retry second pass will handle the embed
                        // stage. Re-entering would DELETE the PendingEmbedding
                        // chunks (via Commit 1's source-path purge) and waste
                        // 20+ minutes of OCR.
                        var fileState = await _store.GetFileStateAsync(storagePath);
                        if (fileState is not null
                            && fileState.Hash == hash
                            && fileState.Status.ShouldSkipOnHashMatch())
                        {
                            if (fileState.Status == FileIndexStatus.NeedsAction)
                                needsAction++;
                            else
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
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // HttpClient timeouts during OCR or any other failure: treat as extract failure.
                        // The `when` clause above ensures real user cancellation still propagates.
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

                    // === Build chunks/infos for Commit 1 ===
                    // From this point on, OCR + chunking + contextualization results
                    // will be persisted to disk BEFORE the embed stage runs, so that
                    // any failure from Stage 4 onward leaves the expensive upstream
                    // work safely on disk for the second pass to resume from.
                    var rawCount = enrichResults.Count(r => !r.IsContextualized);
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
                            // Commit 1 persists every chunk as PendingEmbedding; Commit 2a
                            // promotes to Indexed after EmbedBatchAsync succeeds.
                            Status = ChunkIndexStatus.PendingEmbedding,
                            IsContextualized = enrichResults[i].IsContextualized,
                            LastError = enrichResults[i].FailureReason,
                        };
                    }

                    var pendingFileInfo = new FileWriteInfo
                    {
                        FileHash = hash,
                        Status = FileIndexStatus.PartiallyDeferred,
                        ChunksRaw = rawCount,
                        ChunksPending = chunks.Count,
                    };

                    // === Pre-commit cancellation guard ===
                    // If the user pressed Stop during the long OCR or LLM stages,
                    // catch it here BEFORE persisting. Without this, a cancel that
                    // arrived mid-Stage-1 but was not observed in time would still
                    // flow through to Commit 1 and write chunks the user intended
                    // to discard. Real user cancel re-throws and abort the run.
                    cancellationToken.ThrowIfCancellationRequested();

                    // === Commit 1: Persist staged chunks (PendingEmbedding) ===
                    await _store.PersistChunksAsPendingAsync(
                        storagePath, docChunks, chunkInfos, pendingFileInfo, cancellationToken);

                    // === Stage 4: Embed (with binary-split failure isolation) ===
                    // Happy path is one provider call. When the provider rejects the
                    // batch, EmbedWithBinarySplitAsync recursively halves until it
                    // isolates the offending chunks as size-1 rejections or triggers
                    // a safety guard (max depth or >50% failure ratio). Commit 1 is
                    // already on disk, so any outcome here is safe — the caller
                    // promotes the surviving chunks and marks the rejected ones as
                    // Failed without touching the source's OCR/contextualization.
                    var chunkIds = docChunks.Select(c => c.Id).ToArray();
                    var textsToEmbed = enrichResults.Select(r => r.Text).ToArray();
                    EmbedWithSplitResult splitResult;
                    try
                    {
                        _store.UpdateProgress(fileIndex, files.Count, "embedding",
                            failed, providerHealth);

                        splitResult = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
                            _embeddingProvider, _logger, chunkIds, textsToEmbed, cancellationToken,
                            sourceLabel: storagePath);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (HttpRequestException httpEx)
                    {
                        // Surfaces only if the helper's EmbedBatchAsync call rethrows
                        // via the token-cancelled path or some non-Exception error
                        // escapes — otherwise per-batch rejections are absorbed by
                        // the helper's Exception catch.
                        throw new EmbeddingException(
                            filePath, httpEx.Message, httpEx, statusCode: httpEx.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        throw new EmbeddingException(filePath, ex.Message, ex);
                    }

                    // === Safety guard fallback: leave everything as PendingEmbedding ===
                    if (splitResult.DeferredFallback)
                    {
                        partiallyDeferred++;
                        _logger.LogWarning(
                            "[Indexing] {File} — embedding deferred via safety guard, " +
                            "Commit 1 chunks preserved for next exec retry.", storagePath);
                        logWriter.WriteLine($"[DEFERRED:embed] {storagePath} — binary-split safety guard");
                        continue;
                    }

                    // === Mark binary-split-isolated chunks as Failed first ===
                    // Doing this before Commit 2a keeps file_index.chunks_pending
                    // consistent: after the foreach loop no chunks remain in
                    // PendingEmbedding state, so passing chunksPending: 0 to
                    // PromoteChunksToIndexedAsync is accurate.
                    foreach (var failedId in splitResult.FailedChunkIds)
                    {
                        await _store.UpdateChunkStatusAsync(
                            failedId,
                            Models.ChunkIndexStatus.Failed,
                            _embeddingProvider.ModelId,
                            embedding: null,
                            lastError: "embedding rejected (binary-split isolated)");
                    }

                    // === All chunks rejected: mark the file itself Failed ===
                    if (splitResult.Succeeded.Count == 0)
                    {
                        await _store.MarkFileAsFailedAsync(
                            storagePath,
                            FileIndexStatus.Failed,
                            $"All {splitResult.FailedChunkIds.Count} chunks rejected by embedding provider",
                            "embed");
                        failed++;
                        failedFiles.Add(new FailedFile(
                            storagePath, "embedding provider rejected every chunk (binary-split isolated)"));
                        _logger.LogWarning(
                            "[Indexing] {File} — all {Count} chunks rejected by embedding provider",
                            storagePath, splitResult.FailedChunkIds.Count);
                        logWriter.WriteLine($"[FAILED:embed] {storagePath} — all chunks rejected");
                        continue;
                    }

                    // === Commit 2a: Promote the surviving chunks to Indexed ===
                    var succeededIds = splitResult.Succeeded.Select(s => s.ChunkId).ToArray();
                    var succeededEmbeddings = splitResult.Succeeded.Select(s => s.Embedding).ToArray();
                    var degradedFromContextualization = rawCount > 0;
                    var degradedFromFailedChunks = splitResult.FailedChunkIds.Count > 0;
                    var finalStatus = degradedFromContextualization || degradedFromFailedChunks
                        ? FileIndexStatus.Degraded
                        : FileIndexStatus.Ready;

                    await _store.PromoteChunksToIndexedAsync(
                        storagePath,
                        succeededIds,
                        succeededEmbeddings,
                        _embeddingProvider.ModelId,
                        fileStatus: finalStatus,
                        chunksPending: 0,
                        ct: cancellationToken);

                    if (finalStatus == FileIndexStatus.Degraded) degraded++;
                    indexed++;
                    totalChunks += splitResult.Succeeded.Count;

                    var splitNote = splitResult.FailedChunkIds.Count > 0
                        ? $" ({splitResult.FailedChunkIds.Count} failed via split)"
                        : "";
                    var line = $"[Index] {Path.GetFileName(filePath)} — {chunks.Count} chunks" +
                               (rawCount > 0 ? $" ({rawCount} raw)" : "") +
                               splitNote +
                               $", total={fileSw.ElapsedMilliseconds}ms";
                    _logger.LogInformation("{Line}", line);
                    logWriter.WriteLine(line);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
                    // Stage 4 failure. In the 2-commit model, Commit 1 has already
                    // persisted the chunks as PendingEmbedding and file_index as
                    // PartiallyDeferred. We do NOT call MarkFileAsFailedAsync here —
                    // that would redundantly UPDATE the same row we just wrote.
                    partiallyDeferred++;

                    if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        // Authentication / authorization failure is not transient —
                        // flag the provider as unavailable so subsequent files in
                        // this run skip Stage 4 and rely on the second pass for
                        // eventual recovery once the user fixes their credentials.
                        providerHealth = ProviderHealth.EmbeddingUnavailable;
                        _logger.LogError(ex,
                            "[Indexing] Embedding provider auth failed ({StatusCode}) for {File}. " +
                            "Commit 1 state preserved, deferred retry will resume after credentials fixed.",
                            ex.StatusCode, storagePath);
                    }
                    else
                    {
                        _logger.LogWarning(ex,
                            "[Indexing] Embedding failed for {File} (status={StatusCode}), " +
                            "chunks preserved as PendingEmbedding for deferred retry",
                            storagePath, ex.StatusCode?.ToString() ?? "transport");
                    }

                    logWriter.WriteLine(
                        $"[DEFERRED:embed] {storagePath} — " +
                        $"{ex.StatusCode?.ToString() ?? "transport"}: {ex.Message}");
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

            // === Deferred retry second pass ===
            // Picks up files still in PartiallyDeferred state, including any
            // left over from previous exec runs. Uses the same binary-split
            // helper as the main loop, so per-chunk failure isolation is free.
            var (retriedIndexed, retriedDegraded, retriedFailed, healthAfter) =
                await RunDeferredRetryPassAsync(providerHealth, cancellationToken);
            providerHealth = healthAfter;
            indexed += retriedIndexed;
            degraded += retriedDegraded;
            failed += retriedFailed;

            // Recompute partiallyDeferred from the DB so the summary reflects
            // whatever the second pass left in PartiallyDeferred state — which
            // may differ from the main-loop counter (second pass may have
            // promoted files from previous execs that this run never touched).
            partiallyDeferred = await _store.CountFilesByStatusAsync(
                FileIndexStatus.PartiallyDeferred);

            totalSw.Stop();
            var duration = totalSw.Elapsed;

            var retriedSuffix = (retriedIndexed + retriedDegraded + retriedFailed) > 0
                ? $" (deferred-retry: +{retriedIndexed} indexed, +{retriedDegraded} degraded, +{retriedFailed} failed)"
                : "";
            var summary = $"[Index] Done — indexed={indexed} skipped={skipped} failed={failed} " +
                          $"degraded={degraded} deferred={partiallyDeferred} " +
                          (needsAction > 0 ? $"needsAction={needsAction} " : "") +
                          $"removed={removed} chunks={totalChunks} elapsed={totalSw.ElapsedMilliseconds}ms" +
                          retriedSuffix;
            _logger.LogInformation("{Summary}", summary);
            logWriter.WriteLine(summary);

            await PersistMetadataAsync(indexed, skipped, failed, degraded, partiallyDeferred,
                failedFiles, duration, providerHealth);

            var exitCode = failed > 0 && indexed == 0 ? 1 : 0;
            return new IndexingResult
            {
                Indexed = indexed, Skipped = skipped, Failed = failed,
                Degraded = degraded, PartiallyDeferred = partiallyDeferred,
                NeedsAction = needsAction,
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
    /// Deferred-retry second pass. After the main loop finishes, this walks
    /// every chunk still in <see cref="Models.ChunkIndexStatus.PendingEmbedding"/>
    /// state (grouped by source file) and re-runs Stage 4 embedding via
    /// <see cref="EmbeddingBatchSplitter"/>. This picks up files that the
    /// main loop deferred this run AND files left deferred by previous runs
    /// so the user does not have to invoke anything explicitly.
    ///
    /// Per-file contract:
    /// <list type="bullet">
    ///   <item><description>Chunks whose <c>retry_count &gt;= MaxEmbeddingRetries</c>
    ///     are marked <see cref="Models.ChunkIndexStatus.Failed"/> without calling the
    ///     provider — they've already been tried enough times.</description></item>
    ///   <item><description>Remaining eligible chunks go through the binary-split
    ///     helper. Successful ones are promoted atomically via
    ///     <c>PromoteChunksToIndexedAsync</c>; rejected ones are marked Failed.
    ///     If all eligible chunks are rejected, the file itself is Failed.</description></item>
    ///   <item><description>On <c>DeferredFallback</c> (safety guard fired), every
    ///     eligible chunk's <c>retry_count</c> is incremented and the file
    ///     stays <see cref="FileIndexStatus.PartiallyDeferred"/> for the next run.</description></item>
    ///   <item><description>On 401/403 (auth failure), <c>ProviderHealth</c> is
    ///     flipped to <see cref="ProviderHealth.EmbeddingUnavailable"/> and the
    ///     pass halts so we don't waste further calls on broken credentials.</description></item>
    /// </list>
    ///
    /// Counters returned are additive to the main loop's tallies. The caller
    /// recomputes <c>partiallyDeferred</c> from the DB afterwards so the
    /// summary reflects whatever state files actually ended up in.
    /// </summary>
    async Task<(int Indexed, int Degraded, int Failed, ProviderHealth Health)> RunDeferredRetryPassAsync(
        ProviderHealth providerHealth,
        CancellationToken ct)
    {
        if (providerHealth == ProviderHealth.EmbeddingUnavailable)
        {
            _logger.LogInformation(
                "[DeferredRetry] Skipping second pass: provider unavailable (flagged by main loop).");
            return (0, 0, 0, providerHealth);
        }

        // Load everything at once, grouped by file. A 2-byte-per-chunk row
        // overhead is fine for the realistic worst case (a few thousand
        // chunks across a handful of files).
        var pending = await _store.GetPendingEmbeddingChunksAsync(maxCount: int.MaxValue);
        if (pending.Count == 0)
            return (0, 0, 0, providerHealth);

        var byFile = pending
            .GroupBy(p => p.SourcePath)
            .ToList();

        _logger.LogInformation(
            "[DeferredRetry] Second pass: {FileCount} file(s), {ChunkCount} pending chunk(s).",
            byFile.Count, pending.Count);

        var (retriedIndexed, retriedDegraded, retriedFailed) = (0, 0, 0);

        foreach (var group in byFile)
        {
            ct.ThrowIfCancellationRequested();

            var sourcePath = group.Key;
            var chunks = group.ToList();

            var exhausted = chunks.Where(c => c.RetryCount >= MaxEmbeddingRetries).ToList();
            var eligible = chunks.Where(c => c.RetryCount < MaxEmbeddingRetries).ToList();

            // Chunks that have burned through their retry budget: mark Failed
            // up front so they don't keep consuming provider calls forever.
            foreach (var chunk in exhausted)
            {
                await _store.UpdateChunkStatusAsync(
                    chunk.Id,
                    Models.ChunkIndexStatus.Failed,
                    _embeddingProvider.ModelId,
                    embedding: null,
                    lastError: $"retry budget exhausted (MaxEmbeddingRetries={MaxEmbeddingRetries})");
            }

            if (eligible.Count == 0)
            {
                // Every pending chunk hit the ceiling → the file as a whole is Failed.
                await _store.MarkFileAsFailedAsync(
                    sourcePath,
                    FileIndexStatus.Failed,
                    $"All {exhausted.Count} pending chunks exhausted retry budget",
                    "embed");
                retriedFailed++;
                _logger.LogWarning(
                    "[DeferredRetry] {File} — all {Count} pending chunks exhausted retries, file marked Failed.",
                    sourcePath, exhausted.Count);
                continue;
            }

            var eligibleIds = eligible.Select(c => c.Id).ToArray();
            var eligibleTexts = eligible.Select(c => c.EnrichedText).ToArray();

            EmbedWithSplitResult splitResult;
            try
            {
                splitResult = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
                    _embeddingProvider, _logger, eligibleIds, eligibleTexts, ct,
                    sourceLabel: sourcePath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException httpEx)
                when (httpEx.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogError(httpEx,
                    "[DeferredRetry] Provider auth failed ({StatusCode}) for {File}. " +
                    "Halting second pass; pending chunks preserved for next exec.",
                    httpEx.StatusCode, sourcePath);
                providerHealth = ProviderHealth.EmbeddingUnavailable;
                break;
            }

            if (splitResult.DeferredFallback)
            {
                // Safety guard fired (max depth or >50% ratio). Bump retry_count
                // on every eligible chunk so we converge on Failed if the
                // underlying condition persists across runs.
                foreach (var chunk in eligible)
                {
                    await _store.UpdateChunkStatusAsync(
                        chunk.Id,
                        Models.ChunkIndexStatus.PendingEmbedding,
                        _embeddingProvider.ModelId,
                        embedding: null,
                        lastError: "deferred retry: safety guard fired");
                }
                _logger.LogWarning(
                    "[DeferredRetry] {File} — safety guard fired, bumped retry_count on {Count} chunks.",
                    sourcePath, eligible.Count);
                continue;
            }

            // Mark binary-split-isolated chunks as Failed BEFORE promoting
            // the survivors — keeps file_index.chunks_pending consistent at 0.
            foreach (var failedId in splitResult.FailedChunkIds)
            {
                await _store.UpdateChunkStatusAsync(
                    failedId,
                    Models.ChunkIndexStatus.Failed,
                    _embeddingProvider.ModelId,
                    embedding: null,
                    lastError: "embedding rejected (binary-split isolated)");
            }

            if (splitResult.Succeeded.Count == 0)
            {
                await _store.MarkFileAsFailedAsync(
                    sourcePath,
                    FileIndexStatus.Failed,
                    $"All {splitResult.FailedChunkIds.Count} pending chunks rejected by embedding provider",
                    "embed");
                retriedFailed++;
                _logger.LogWarning(
                    "[DeferredRetry] {File} — all {Count} pending chunks rejected, file marked Failed.",
                    sourcePath, splitResult.FailedChunkIds.Count);
                continue;
            }

            var succeededIds = splitResult.Succeeded.Select(s => s.ChunkId).ToArray();
            var succeededEmbeddings = splitResult.Succeeded.Select(s => s.Embedding).ToArray();
            var anyFailed = splitResult.FailedChunkIds.Count > 0 || exhausted.Count > 0;
            var finalStatus = anyFailed ? FileIndexStatus.Degraded : FileIndexStatus.Ready;

            await _store.PromoteChunksToIndexedAsync(
                sourcePath,
                succeededIds,
                succeededEmbeddings,
                _embeddingProvider.ModelId,
                fileStatus: finalStatus,
                chunksPending: 0,
                ct: ct);

            if (finalStatus == FileIndexStatus.Degraded) retriedDegraded++;
            else retriedIndexed++;

            _logger.LogInformation(
                "[DeferredRetry] {File} — promoted {Succeeded}/{Eligible} (failed={Failed}, exhausted={Exhausted}), " +
                "file → {Status}",
                sourcePath, splitResult.Succeeded.Count, eligible.Count,
                splitResult.FailedChunkIds.Count, exhausted.Count, finalStatus);
        }

        _logger.LogInformation(
            "[DeferredRetry] Pass complete: indexed={Indexed} degraded={Degraded} failed={Failed}",
            retriedIndexed, retriedDegraded, retriedFailed);

        return (retriedIndexed, retriedDegraded, retriedFailed, providerHealth);
    }

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
