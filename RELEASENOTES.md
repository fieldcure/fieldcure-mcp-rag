# Release Notes

## v2.1.0 (2026-04-20)

### Changed

- **Cross-platform hardening of the OCR fallback** — the Tesseract-based OCR path is now enforced Windows-only at the package level instead of purely at runtime:
  - `FieldCure.DocumentParsers.Ocr` is referenced via an MSBuild `Condition="$([MSBuild]::IsOSPlatform('Windows'))"`.
  - A `WINDOWS_OCR` compile symbol gates the `AddOcrSupport` call and excludes `LazyOcrEngine.cs` from non-Windows builds.
  - Linux and macOS builds are pure managed code with no Tesseract native binaries. Scanned PDFs without a text layer yield empty text on those platforms; text-layer PDFs work normally everywhere.
- **DocumentParsers 1.x → 2.x** — PDF text extraction (PdfPig) is now part of the core `FieldCure.DocumentParsers` package; the dedicated `FieldCure.DocumentParsers.Pdf` reference is removed. `FieldCure.DocumentParsers.Pdf.Ocr` was renamed to `FieldCure.DocumentParsers.Ocr`. Public MCP tool surface is unchanged.
- **Credential resolution per mode** — `serve` (stdio) uses env var → MCP Elicitation with a session cache and a 2-attempt re-elicit cap. `exec` and `exec-queue` (headless batch) use env var only and soft-fail with a clear message when unset. See [ADR-001](https://github.com/fieldcure/fieldcure-assiststudio/blob/main/docs/ADR-001-MCP-Credential-Management.md) Phase 3a.

### Added

- **Ubuntu CI job** — `.github/workflows/ci.yml` now builds against both `windows-latest` and `ubuntu-latest` so the cross-platform guarantee is regression-tested every push.
- **XML doc tags** filled in across the source and test trees — every public and private method/type surface now carries `/// <summary>` where previously absent.

### Fixed

- **`SearchDocumentsTool` / `GetDocumentChunkTool`** class-level XML doc blocks had been placed after `[McpServerToolType]`; moved above the attribute so the compiler attaches them to the class. No runtime change.

---

## v2.0.0 (2026-04-17)

### Breaking

- **`unload_kb` tool removed** — KB cache eviction is now lazy (config.json deletion triggers automatic cleanup on next access)
- **API keys via environment variables** — `CredentialService` (Windows Credential Manager P/Invoke) removed. API keys are resolved from environment variables (e.g., `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`). AssistStudio injects them from PasswordVault into child process environments. Standalone callers set env vars directly.
- **`ChannelFactory.Create()` no longer requires `CredentialManager`** parameter

### Added

- **Queue-based indexing orchestrator** — all indexing requests flow through `start_reindex` MCP tool. Single orchestrator processes entries sequentially (no GPU contention). Scope merge rules (full ⊃ contextualization ⊃ embedding), force/deferred flags, PID-based lock with reuse defense.
- **`cancel_reindex` tool** — removes pending queue entries via MCP
- **`prune-orphans` CLI** — deletes orphan KB folders (GUID-named, no config.json). Protected folders (`.`, `_` prefix, `-backup-`) never touched.
- **`--sweep-all` flag for `exec-queue`** — processes deferred entries too (used at app shutdown)
- **Ollama native embedding provider** — `OllamaEmbeddingProvider` calls `/api/embed` (not `/v1/embeddings` shim) with `keep_alive` parameter. Requires Ollama 0.4.0+.
- **Ollama native contextualizer** — `OllamaChunkContextualizer` calls `/api/chat` with `keep_alive` and `options.num_ctx`. 10-minute HttpClient timeout for cold model loads.
- **`OllamaDefaults`** — `KeepAlive = "5m"`, `NumCtx = 8192` constants
- **`ProviderConfig.KeepAlive` / `NumCtx`** — per-KB override for Ollama parameters in config.json
- **`FolderClassification` + `Classify()`** — shared folder classification for `ListKbs` and `prune-orphans`
- **`MultiKbContext.BasePath`** — public read-only property
- **Lazy unload in `GetKb()`** — detects config.json deletion and auto-evicts cached instance

### Changed

- **`get_index_info` includes queue state** — `status` field (`ready`/`queued`/`indexing`/`failed`), `queue` object with position/deferred/last_error
- **`check_changes` / `get_index_info` promoted to AI-visible** — "Internal tool" / "Do not call" removed from descriptions
- **Orchestrator lock → separate file** — `orchestrator.lock` (PID + started_at) instead of embedded in queue JSON. PID reuse defense via `Process.StartTime` comparison.
- **Cross-platform** — `net8.0` TFM is no longer misleading; `#pragma warning disable CA1416` removed. Tesseract OCR loads lazily on first scanned PDF (Windows only); non-Windows platforms silently skip scanned pages but process text-layer PDFs normally.

### Removed

- `CredentialService.cs`, `ICredentialService.cs` (Windows Credential Manager P/Invoke)
- `EmbeddingProviderFactory.cs` (dead code, never called)
- `UnloadKbTool.cs`
- `DeferredQueueLock` from queue JSON (moved to `orchestrator.lock` file)

---

## v1.5.0 (2026-04-16)

### Added

- **Partial re-index (`--partial=contextualization|embedding`)** — when only the contextualizer or embedding model changes, OCR/chunking results are preserved and only downstream stages re-run. Scanned 600-page PDF: full re-index 20+ min, partial embedding ~2 min. Includes cancel-resume support (interrupted partial runs resume from remaining pending chunks) and cross-kind safety (mismatched partial flag on interrupted state produces a clear error with `--force` escape).
- **`search_mode` parameter on `search_documents`** — `"auto"` (default, hybrid when possible), `"bm25"` (keyword-only, no embedding), `"vector"` (semantic-only). Tool description instructs AI models not to override the default. `"vector"` with no embedding provider throws `InvalidOperationException`.
- **`unload_kb` tool** — evicts a cached KB instance and releases its SQLite connection so the host can delete KB files without retry loops. Idempotent, no-op if not loaded.
- **Chunker pre-validation** — re-splits any chunk exceeding a conservative character-based upper bound (default 4,000 chars, configurable via `embedding.max_chunk_chars`) during chunking, before embedding is attempted. Uses sentence boundaries when possible, hard cut otherwise. Binary-split fallback remains as safety net.
- **Embedding batch size resolution** — static lookup table maps common provider/model combinations to safe initial batch sizes (e.g., `ollama:qwen3-embedding:8b` = 64). Configurable via `embedding.batch_size`. Eliminates unnecessary first-call failures and binary-split activations. KB-3 (1025 chunks, Ollama): 210s with binary-split → 131s without.
- **Contextualization diagnostics** — multi-line log format when contextualization fails (`failed: N/M, falling back to raw`), `chunks_contextualized` column in `file_index` (schema v2), `total_chunks_contextualized` / `total_chunks_raw` / `files_contextualization_degraded` in `get_index_info`, and `is_contextualization_degraded` flag in `check_changes` (not folded into `is_clean`).
- **`PendingContextualization` chunk status (5)** — tracks chunks that need re-contextualization during partial re-index.
- **`GetPendingChunkCountsAsync`** — efficient kind-aware query for partial resume detection.

### Changed

- **Counter semantics cleanup** — in-memory counters split into per-run deltas (`indexedThisRun`, `skippedThisRun`) and DB state snapshots (`GetStateCountersAsync`). Summary log now shows two lines: `Run: indexed=N skipped=M ...` and `State: failed=N degraded=M ...`. Fixes misleading `degraded=0` on subsequent runs.
- **`IndexingEngine.RunAsync` signature** — now accepts optional `string? partial` parameter.
- **`EmbedWithBinarySplitAsync`** — accepts optional `batchSize` parameter; splits input into batch-sized windows before attempting API calls.
- **Cancel file cleanup** — moved to `Program.cs` top-level `finally` block for guaranteed cleanup regardless of exit path.
- **Schema version bump** — `TargetUserVersion` 1 → 2.

### config.json additions

| Field | Section | Description |
|-------|---------|-------------|
| `max_chunk_chars` | `embedding` | Max chars per chunk (default: 4000) |
| `batch_size` | `embedding` | Max chunks per API call (default: table lookup) |

---

## v1.4.2 (2026-04-15)

### Fixed

- **v1.4.0 silent data loss on first-time embedding failure** — closes the Known Issue recorded in v1.4.1. When a newly added file (no prior `file_index` row) failed at Stage 4 embedding, the old code caught `EmbeddingException` and called `MarkFileAsFailedAsync`, which was an `UPDATE`-only operation and a no-op when the row did not yet exist. The extracted text and contextualized chunks were silently dropped, the `last_partially_deferred_count` counter was incremented anyway, and the file became invisible to search until the user manually intervened. The next exec would re-run the full pipeline (OCR + contextualization + embedding) and hit the same error — an unbounded loop of wasted work. The 2-commit model described below prevents this structurally: chunks are persisted as `PendingEmbedding` *before* the embedding call is issued, so any Stage 4 outcome — success, per-chunk rejection, network failure, process crash, power loss — leaves the upstream work on disk for the deferred retry pass to pick up.

### Added

- **2-commit indexing pipeline** — every file goes through an explicit persist step between Stages 3 and 4. `SqliteVectorStore.PersistChunksAsPendingAsync` writes the full chunk list and a `file_index` row (status = `PartiallyDeferred`, `chunks_pending = N`) in a single transaction using `INSERT ... ON CONFLICT`, so the row is created whether or not a prior one existed. Only after this Commit 1 succeeds does the engine issue the embedding call. On success, `PromoteChunksToIndexedAsync` performs Commit 2a atomically: chunks flip to `Indexed`, embeddings are inserted, and the `file_index` row transitions to `Ready` (or `Degraded` when any chunk was rejected). On failure, Commit 1 state is left alone — the deferred retry pass will take over.
- **Binary-split per-chunk failure isolation (`EmbeddingBatchSplitter`)** — when the embedding provider rejects a batch, the splitter recursively halves until it either finds a working sub-batch or isolates the offending chunks at size-1. A single oversized chunk (e.g., a math-dense page that exceeds `text-embedding-3-large`'s 8192-token per-input limit) is marked `Failed` and the rest of the file is promoted to `Indexed`. The file's final status is `Degraded` — partially searchable rather than completely missing. The algorithm is provider-agnostic (driven by `EmbedBatchAsync` exceptions, no need to parse error strings) and costs nothing on the happy path: when all chunks succeed on the first try, the helper issues exactly one provider call. For N chunks with one failure, expect approximately `2 × log₂(N)` provider calls. Verified end-to-end against a real 850-chunk PDF: chunk 846 isolated in 21 provider calls across 10 split depths.
- **Automatic deferred retry pass (`RunDeferredRetryPassAsync`)** — at the end of every `exec` run, after the main loop, the engine walks every chunk still in `PendingEmbedding` state (grouped by source file) and re-runs Stage 4 via the same binary-split helper. This picks up files deferred this run AND files left deferred by previous runs, so the user no longer has to trigger retries manually. Per chunk, the retry budget is `IndexingEngine.MaxEmbeddingRetries = 3`; chunks that exhaust it are marked `Failed` without another provider call. On 401/403 the pass halts immediately and flags `ProviderHealth.EmbeddingUnavailable` so the rest of the queue is preserved for a future run with fixed credentials.
- **Safety guards on the binary split** — `MaxSplitDepth = 20` caps recursion against pathological inputs, and a top-level failure ratio exceeding 50 % triggers `DeferredFallback = true`, rolling the whole attempt back to `PendingEmbedding` so the next exec can retry with a clean slate. This distinguishes legitimate per-chunk rejections from provider-wide outages (quota, auth, model down).
- **Binary split diagnostic logging** — `[BinarySplit] start` / `done` lifecycle lines at Info level (silent on the happy path), per-depth `attempt` / `OK` / `FAILED` trace at Debug level, and per-chunk terminal failures at Warning level with the exact chunk id and provider error body. The `done` line carries `promoted`, `failed`, `fallback`, `depthMax`, and `providerCalls` so cost can be reconstructed from logs without a debugger. Absolute chunk-index offsets are used throughout (`range=[846..846]`, not `range=[0..0]`), so a failing chunk can be located in the source file directly.
- **`--verbose` / `-v` flag for `exec` mode** — raises the logger minimum level from Information to Debug so the per-depth binary-split trace becomes visible. Default stays at Information so normal runs remain quiet.
- **Status-aware hash-skip (`FileIndexStatusExtensions.ShouldSkipOnHashMatch`)** — the main loop's hash-skip decision is now centralized in one extension method. Every current status returns `true` (for different reasons: `Ready` and `Degraded` are fully persisted, `PartiallyDeferred` has Commit 1 state that the second pass will pick up, `Failed` and `NeedsAction` need explicit user action). The `default` arm returns `false` so any future status is forced to make an explicit choice. `NeedsAction` files surface through a dedicated counter instead of being lumped into `skipped`.
- **OCE `when`-guard on Stage 1 / Stage 4 exceptions** — `FileExtractionException` and `EmbeddingException` catches now use `when (cancellationToken.IsCancellationRequested)` to distinguish genuine user cancellation (which re-throws) from HttpClient timeouts disguised as `OperationCanceledException` (which are treated as normal stage failures). A 100-second OCR timeout used to abort the entire run; now it just fails the file and moves on.

### Changed

- **Deferred retry behavior (breaking behavior change)** — `PartiallyDeferred` files no longer require manual intervention. In v1.4.0 / v1.4.1, a deferred file stayed deferred until the host surfaced it and the user triggered a re-index; the deferred retry pass existed only as an API. In v1.4.2, every `exec` run automatically walks the pending chunks and retries embedding. Hosts that currently display "deferred files" as a user action should instead treat them as transient — they will resolve themselves on the next indexing run unless they exceed the retry budget. Combined with v1.4.1's `is_clean` extension (which already folded `is_schema_stale` into the clean check), the end-to-end semantic is: `check_changes.is_clean == true` now means both "no content changes" and "no schema migration pending", and the deferred retry pass handles the embed backlog in the background. AssistStudio is effectively the only consumer; host-side expectations around deferred files need to be revisited.
- **`MarkFileAsFailedAsync` return type** — now `Task<bool>` returning `true` when an existing row was updated and `false` when no row existed. Callers in `IndexingEngine.RunAsync` use the return value to log a "stuck-new-file" warning so the situation is visible even before the next `check_changes` call. Existing external callers that ignore the return value keep compiling; no source-breaking change for typical use.
- **Per-file `[Index]` log line** — now reads `(N failed via split)` instead of `(N rejected)` when the binary split isolated chunks, making the failure mode obvious at a glance in `index_timing.log`.
- **`IndexingEngine.PersistMetadataAsync` counter sanity check removed** — the check compared per-run delta counters against whole-DB totals, which are different quantities, and spuriously warned every second run after a legitimate `Degraded` transition. The v1.4.0 `MarkFileAsFailedAsync` no-op bug it was meant to catch is now prevented structurally by the 2-commit model, so the check has no remaining failure mode to guard against. The counter delta-vs-state semantic mismatch in the summary line is known and tracked for a later release.

### Known Limitations

- **~~Single chunk exceeding the embedding model's per-input token limit is marked `Failed`, not rechunked~~** — resolved in v1.5.0. `TextChunker` now enforces a `maxChars` upper bound (default 4000) that pre-splits oversized chunks before they reach Stage 4.

### Tests

- 19 new unit tests across `EmbeddingBatchSplitterTests` (14), store additions (PersistChunksAsPendingAsync / PromoteChunksToIndexedAsync / GetFileStateAsync / CountFilesByStatusAsync), and `FileIndexStatusExtensions`. Total 133 → 174 passing. Splitter coverage includes the happy path silence contract, single-bad-chunk isolation, cancellation rethrow, ArgumentException on mismatched counts, 401/403 bubble-up (must not be swallowed into per-chunk rejections), and the absolute-range-offset invariant for the per-chunk Warning line.
- End-to-end verification against `kb-2` with the real 1999 `Introduction to Electrodynamics.pdf` (850 chunks, chunk 846 over the token limit). Run 1: 11 previously-indexed files hash-skipped, 1999 PDF went through the full 2-commit pipeline, binary-split isolated chunk 846 in 21 provider calls at depth 10, file transitioned to `Degraded` with 849 / 850 chunks indexed. Run 2: all 12 files hash-skipped in 85 ms with no warnings — the expensive upstream work is preserved and will never be re-run for this file without `--force`.

---

## v1.4.1 (2026-04-15)

### Fixed

- **`MultiKbContext.ListKbs()` loose folder scanning** — previously any subfolder under the base path with a `config.json` was accepted as a KB, so user backup folders (e.g., `<kb-id>.backup-<ts>`) showed up as duplicate KBs in `list_knowledge_bases` and the host UI. Four guards are now applied per folder in order: (1) prefix filter — folders starting with `.` or `_` are skipped silently, (2) `config.json` existence, (3) `config.json` parseability (parse failures log a warning instead of being swallowed), (4) `config.Id` ↔ folder name match using `OrdinalIgnoreCase` (mismatches are the copy-backup signature and log a warning before skipping). Per-KB stats reads from `rag.db` are also wrapped — a corrupt DB no longer kills the entire listing, the affected KB just reports zeros with a warning.

### Added

- **`PRAGMA user_version` schema sentinel** — schema state is now recorded in SQLite's built-in `user_version` header field instead of being inferred from `PRAGMA table_info` alone. `SqliteVectorStore.TargetUserVersion = 1` is the target, written at the end of `InitializeSchema()` (which only runs when `readOnly == false`, so the "serve = reader, exec = writer" invariant is preserved). Legacy databases created before v1.4.1 report `user_version = 0` regardless of which actual schema columns they contain.
- **`SqliteVectorStore.GetUserVersion()`** — instance method that reads `user_version` from the store's connection. Cheap (microseconds) — `PRAGMA user_version` reads from page 0 which is already in memory after connection open. Prefer this when the caller already holds a store instance.
- **`SqliteVectorStore.ReadUserVersion(dbPath)`** — static helper that opens the database read-only, calls `GetUserVersion()`, and disposes. For diagnostic tools and tests that don't already have a store.
- **`KbSummary.SchemaVersion` / `KbSummary.IsSchemaStale`** — `list_knowledge_bases` now surfaces the schema version each KB was last tagged with. `IsSchemaStale == true` means the KB was indexed before v1.4.1 (or before whatever future version the code is built against). Stale KBs still serve search queries correctly; re-indexing triggers automatic migration through the exec path.
- **`list_knowledge_bases` response extension** — each KB entry now includes `schema_version` and `is_schema_stale` fields, and the top-level response carries `current_schema_version` so consumers can compare without hardcoding the target.
- **`check_changes` schema staleness fields** — the response now includes `is_schema_stale`, `kb_schema_version`, and `current_schema_version`. `is_schema_stale` is computed as `kb_schema_version < current_schema_version`. Tool description updated to explain what a stale KB means and that re-indexing triggers automatic migration.

### Changed

- **`check_changes.is_clean` semantics (soft breaking)** — `is_clean` now also requires `!is_schema_stale`. The first time a host running v1.4.1 or later calls `check_changes` against a KB that was indexed under v1.4.0 or earlier, the response will be `is_clean: false`, `is_schema_stale: true`, `kb_schema_version: 0` even when no files have changed. The fix is a single re-index per KB (the same remedy as for `is_prompt_stale`), which writes `user_version = 1` through the exec path's `InitializeSchema()`. AssistStudio is effectively the only consumer; host-side message branching will be updated in a follow-up release.
- **`MultiKbContext` constructor** — now accepts an optional `ILogger<MultiKbContext>` parameter (defaults to `NullLogger` when omitted, so existing tests and direct instantiation keep working). `Program.cs` serve mode registers the context through the DI container so the host's logging pipeline supplies the logger; the explicit `Dispose()` at shutdown is no longer needed because the host disposes singletons automatically.
- **`KbSummary` record** — new `SchemaVersion` and `IsSchemaStale` members. Positional record so any direct constructor call outside this assembly needs to supply the new values; in-tree callers are all updated.

### Tests

- 19 new unit and integration tests (total 114 → 133) covering the `user_version` sentinel round-trip, the read-only open invariant ("serve = reader" in test form), all four `ListKbs` guards (including the case-insensitive id match), and end-to-end `check_changes` response shape for both clean tagged and legacy untagged KBs. The integration tests exercise the full `CheckChangesTool → MultiKbContext → SqliteVectorStore` chain with real temp-folder fixtures, so the new JSON fields are validated at the wire level rather than only in isolation.

### Known Issues

- **Deferred embedding retry for first-indexed files is not persisted** — v1.4.0 introduced stage-level failure handling with a `PartiallyDeferred` counter and the infrastructure for retrying chunks whose embedding failed (`ChunkIndexStatus.PendingEmbedding`, `GetPendingEmbeddingChunksAsync`, `UpdateChunkStatusAsync`). That path works correctly when an *already indexed* file fails on re-indexing — `ReplaceFileChunksAsync`'s transaction rolls back and previous chunks are preserved. But when a *newly added* file fails embedding on its first pass, `IndexingEngine` catches `EmbeddingException` and calls `MarkFileAsFailedAsync`, which is an `UPDATE` against `file_index` and therefore a no-op when no prior row exists — the extracted text and contextualized chunks are silently dropped. The `last_partially_deferred_count` counter is still incremented, so the log and metadata will say "1 deferred" while the database contains nothing for that file. Result: the file is invisible to search and will be retried from scratch on every subsequent exec, hitting the same error. Tracked separately; will be addressed in a later release by (a) extending `IndexingEngine` to persist chunks with `PendingEmbedding` status when embedding fails and (b) wiring `GetPendingEmbeddingChunksAsync` / `UpdateChunkStatusAsync` into a deferred retry pass.

---

## v1.4.0 (2026-04-14)

### Added

- **Stage-level failure handling** — indexing pipeline now distinguishes extract, contextualize, and embed failures with dedicated exception types (`FileExtractionException`, `EmbeddingException`) and separate counters (`Failed`, `Degraded`, `PartiallyDeferred`)
- **Atomic file replace** — `ReplaceFileChunksAsync` wraps DELETE + INSERT + file_index upsert in a single SQLite transaction; embedding failure preserves previous data instead of causing data loss
- **Schema migration (v1.3 → v1.4)** — `AddColumnIfMissing` helper + `MigrateV04StatusColumns` adds 12 columns across `chunks`, `file_index`, and `_indexing_lock` tables with partial index `idx_chunks_status`
- **Chunk/file status enums** — `ChunkIndexStatus` (Indexed, IndexedRaw, PendingEmbedding, PendingExtraction, Failed) and `FileIndexStatus` (Ready, Degraded, PartiallyDeferred, NeedsAction, Failed)
- **`EnrichResult` return type** — `IChunkContextualizer.EnrichAsync` returns structured result with `IsContextualized`, `FailureReason`, `FailureType` instead of silently returning original text
- **Contextualization logging** — failed chunks now emit `LogWarning` with chunk index and exception details; `OperationCanceledException` propagated instead of swallowed
- **`IndexingResult` record** — `RunAsync` returns structured result replacing `int` exit code, with `Indexed`, `Skipped`, `Failed`, `Degraded`, `PartiallyDeferred`, `Duration`, and `ExitCode`
- **`ProviderHealth` enum** — tracks embedding/contextualizer availability in `_indexing_lock.provider_health` for real-time monitoring
- **Store API additions** — `MarkFileAsFailedAsync`, `GetPendingEmbeddingChunksAsync`, `UpdateChunkStatusAsync`, `ChunkWriteInfo`, `FileWriteInfo`, `PendingChunk`
- **Extended `UpdateProgress`** — now accepts `currentStage`, `failedCount`, `providerHealth` with COALESCE null-preserving semantics
- **Extended `get_index_info`** — 7 new response fields: `last_indexed_count`, `last_skipped_count`, `last_degraded_count`, `last_partially_deferred_count`, `last_run_duration_ms`, `last_run_completed_utc`, `last_provider_health`
- **Design doc** — `docs/rag-fallback-design.md` covering pipeline architecture, exec/SQLite IPC, chunk state machine, and failure handling

### Removed

- **`SetFileHashAsync`** — replaced by `FileWriteInfo` in `ReplaceFileChunksAsync` for atomic file_index updates

### Changed

- **`IChunkContextualizer.EnrichAsync`** signature — `Task<string>` → `Task<EnrichResult>` (breaking for custom implementations)
- **`IndexingEngine.RunAsync`** return type — `Task<int>` → `Task<IndexingResult>`
- **Contextualizer constructors** — `AnthropicChunkContextualizer` and `OpenAiChunkContextualizer` accept optional `ILogger` parameter

---

## v1.3.1 (2026-04-08)

### Fixed

- **Tesseract native DLL missing in dotnet tool** — upgraded `DocumentParsers.Pdf.Ocr` to 1.0.1 which includes `leptonica-1.82.0.dll` and `tesseract50.dll` via `buildTransitive/.targets`, fixing `DllNotFoundException` on server startup

---

## v1.3.0 (2026-04-08)

### Added

- **OCR fallback for scanned PDFs** — pages with no extractable text are rendered at 300 DPI and processed via Tesseract OCR (English + Korean, tessdata_fast)
- `FieldCure.DocumentParsers.Pdf.Ocr` 1.x dependency with engine pool for concurrent OCR

---

## v1.2.1 (2026-04-08)

### Fixed

- **`is_clean` excludes failed files** — failed files cannot be fixed by re-indexing, so `check_changes` no longer reports them as dirty; prevents misleading "re-index needed" status

---

## v1.2.0 (2026-04-08)

### Added

- **`check_changes` tool** — dry-run filesystem scan that compares source files against the index; returns added/modified/deleted/failed file counts and paths with `is_prompt_stale` and `is_clean` flags
- **`last_indexed_at` in `get_index_info`** — most recent indexing timestamp (ISO 8601 UTC) from `file_index`
- **Failed file tracking** — indexing persists `last_failed_count`, `last_failed_files`, and `last_failed_reasons` to `index_metadata`; `get_index_info` returns these fields; `check_changes` separates known-failed files from "added"

### Fixed

- **Progress double-increment** — `fileIndex++` was called in both try and finally blocks, causing progress to exceed total (e.g. 12/8 for 8 files)
- **Missing failure log** — failed file path and exception now written to `index_timing.log`

### Changed

- `IndexingEngine.SupportedExtensions` — now `internal static` for tool reuse

---

## v1.0.0 (2026-04-06)

### Stable Release

First stable release. Tool signatures, search behavior, and config.json schema are now covered by semver.

### Changed

- **Tool annotations completed** — all 4 tools declare `ReadOnly=true`, `Destructive=false`, `Idempotent=true`
- `FieldCure.DocumentParsers` 1.x — DOCX, HWPX, XLSX, PPTX, PDF text extraction
- `FieldCure.DocumentParsers.Pdf` 1.x

### Improved

- Added XML doc comments to all private/static methods (Program.cs, IndexingEngine, SqliteVectorStore)

---

## v0.12.2 (2026-04-03)

### Changed

- `ModelContextProtocol` 1.1.0 → 1.2.0

---

## v0.12.1 (2026-04-03)

### Fixed

- **Re-release of v0.12.0** — v0.12.0 nupkg contained stale v0.10.1 DLL due to `dotnet pack --no-build` using cached Release build
- **Publish script** — removed `--no-build` shortcut; now performs `dotnet clean` + full rebuild before packing

---

## v0.12.0 (2026-04-03)

### Breaking Changes

- **Multi-KB serve mode** — `serve --path <kb-path>` replaced with `serve --base-path <path>`, serving all KBs under the base path with a single process
- **`kb_id` parameter required** — `search_documents`, `get_document_chunk`, `get_index_info` now require a `kb_id` parameter
- **`RagContext` removed** — replaced by `MultiKbContext` (lazy-loading, concurrent KB management)

### Added

- **`MultiKbContext`** — manages multiple knowledge bases under a shared base path with `ConcurrentDictionary` lazy loading; auto-cleans cached instances for deleted KBs
- **`list_knowledge_bases` tool** — returns all available KBs with ID, name, file/chunk counts, and indexing status
- **Read-only `SqliteVectorStore`** — `readOnly` parameter skips schema initialization and opens DB in read-only mode for serve
- **`kb_id` in tool responses** — all tool responses now include `kb_id` for identification

### Changed

- `get_index_info` response: added `kb_name`, removed `default_prompt` and `contextualizer` fields

---

## v0.11.1 (2026-04-03)

### Changed

- `FieldCure.DocumentParsers` 0.3.x → 0.* (picks up 0.4.x with improved Markdown parsing)
- `FieldCure.DocumentParsers.Pdf` 0.2.x → 0.*

---

## v0.11.0 (2026-04-03)

### Breaking Changes

- **exec/serve dual-mode architecture** — single-process MCP server replaced with `exec` (headless indexing) + `serve` (search-only MCP server)
- **CLI interface changed** — `fieldcure-mcp-rag <context-folder>` → `fieldcure-mcp-rag serve --path <kb-path>` / `exec --path <kb-path>`
- **`index_documents` tool removed** — indexing is now handled by `exec` mode as a separate process
- **Environment variables removed** — `EMBEDDING_*` and `CONTEXTUALIZER_*` env vars replaced by `config.json` + PasswordVault credentials
- **DB file renamed** — `rag_index.db` → `rag.db`
- **Data path changed** — `{folder_hash}` based → `{kb-id}` (UUID) based, app creates the folder

### Added

- **`exec` mode** — headless indexing process with exit codes (0=success, 1=failure, 2=cancelled)
- **`serve` mode** — search-only MCP server (3 tools: `search_documents`, `get_document_chunk`, `get_index_info`)
- **`config.json`** — per-knowledge-base configuration (source paths, contextualizer/embedding model settings)
- **`CredentialService`** — Windows PasswordVault integration for API key resolution (shared with AssistStudio)
- **`IndexingEngine`** — extracted from `IndexDocumentsTool` with cancel file support and multiple source paths
- **Cancel file** — `{kb-path}/cancel` triggers graceful shutdown of exec process
- **Multiple source paths** — single knowledge base can index from multiple folder sources

---

## v0.10.1 (2026-03-28)

### Added

- **File count safety limits** — `index_documents` enforces soft limit (1,000 files → warning) and hard limit (10,000 files → error with hint to specify a subfolder)

---

## v0.10.0 (2026-03-27)

### Added

- **PDF indexing support** — `.pdf` files now indexed and searchable via `FieldCure.DocumentParsers.Pdf`; text extracted page-by-page with `## Page N` headers
- **Math equation indexing** — DOCX and HWPX math equations extracted as `[math: LaTeX]` blocks via `FieldCure.DocumentParsers` 0.3.x; equations are now searchable in the RAG index

### Changed

- `FieldCure.DocumentParsers` 0.2.x → 0.3.x
- `FieldCure.DocumentParsers.Pdf` 0.1.x → 0.2.x
- `ModelContextProtocol` 0.2.x → 1.1.0

---

## v0.9.0

Cross-process indexing lock for multi-tab safety.

- **`_indexing_lock` table** — SQLite singleton-row mutex prevents concurrent indexing from multiple processes on the same DB
- **`AcquireLock` / `ReleaseLock`** — `index_documents` acquires lock before indexing, releases in `finally`; returns error if another live process holds the lock
- **`UpdateProgress`** — per-file progress written to lock row during indexing
- **Stale lock detection** — both `AcquireLock` and `GetLockInfo` check if lock holder PID is alive; dead processes are auto-cleaned
- **`get_index_info` returns `is_indexing`** — includes `indexing_progress` object (`current`, `total`, `pid`) when another process is indexing
- **Exception-free PID check** — uses `Process.GetProcesses()` instead of `Process.GetProcessById()` to avoid first-chance `Win32Exception`

## v0.8.0

MCP progress notifications for indexing operations.

- **Indexing progress notifications** — `index_documents` reports per-file progress via MCP `notifications/progress` protocol; clients passing a `progressToken` receive `(current, total, message)` updates for determinate progress bar display
- **`IProgress<ProgressNotificationValue>` injection** — SDK auto-binds the progress reporter; no manual `McpServer` handling needed in the tool

## v0.7.0

Parallel contextualization, shared constants, and indexing diagnostics.

- **Parallel chunk contextualization** — `Parallel.ForEachAsync` with `MaxDegreeOfParallelism=4` for contextualizer API calls; ~4.5x speedup (172s → 38s on 41 chunks with gpt-4o-mini)
- **NullChunkContextualizer fast path** — skips parallel overhead when no contextualizer model is configured
- **Indexing timing log** — per-file and per-stage diagnostics written to `{dataRoot}/index_timing.log` (parse, chunk, context, embed, store)
- **Shared `DefaultMaxTokens` constant** — contextualizer max_tokens raised from 300 → 500 to prevent KEYWORDS truncation on bilingual outputs
- **Renamed `SystemPrompt` → `DefaultSystemPrompt`** — clearer naming for the built-in default prompt constant

## v0.6.0

Per-folder system prompt with DB persistence and stale-index detection.

- **`index_metadata` table** — key-value store in SQLite for index configuration
- **Per-folder system prompt** — `index_documents` accepts optional `system_prompt` parameter; stored in DB per folder
- **Prompt priority chain** — tool parameter > DB stored value > env var > built-in default
- **Stale-index detection** — `effective_prompt_hash` stored in DB; `get_index_info` returns `is_prompt_stale` when built-in prompt has been updated since last indexing
- **`get_index_info` tool** — returns folder stats, prompt configuration, and stale detection flag (internal, for host application use)
- **`IChunkContextualizer.SystemPrompt` property** — runtime-settable on all contextualizer implementations
- **Smart default handling** — only custom prompts stored in DB; null = use built-in default (code updates auto-apply on re-index)

## v0.5.0

- **Improved default contextualization prompt** — bilingual keyword extraction (original language + English), domain-specific concept extraction, grammatical suffix removal with concrete example
- **Custom system prompt** — `CONTEXTUALIZER_SYSTEM_PROMPT` environment variable for domain-specific keyword extraction (empty = built-in default)

## v0.4.0

- **Fix DocumentParsers dependency** — pin to `0.2.*` to ensure XLSX/PPTX/HWPX parsers are included (v0.3.0 bundled v0.1.0 due to `Version="*"` resolving before NuGet indexing)

## v0.3.0

AI-powered chunk contextualization and .NET 8.0 migration.

- **Chunk contextualization** — AI-generated context descriptions and normalized keywords per chunk during indexing (`IChunkContextualizer`)
- **OpenAI-compatible contextualizer** — supports Ollama, OpenAI, Groq, and any `/v1/chat/completions` endpoint (`OpenAiChunkContextualizer`)
- **Anthropic contextualizer** — supports Claude Haiku, Sonnet, Opus via `/v1/messages` endpoint (`AnthropicChunkContextualizer`)
- **Enriched search** — FTS5 and vector embedding use enriched text; original content preserved for responses
- **Graceful degradation** — contextualization disabled when `CONTEXTUALIZER_MODEL` is empty (v0.2.0 behavior)
- **Error tolerance** — AI failures fall back to original chunk text without interrupting indexing
- **v0.2.0 DB migration** — `enriched` column added automatically via `ALTER TABLE`; existing chunks initialized with original content
- **net9.0 → net8.0** — unified target framework with FieldCure.DocumentParsers
- **XLSX / PPTX support** — via FieldCure.DocumentParsers v0.2.0 (Excel spreadsheets and PowerPoint presentations)

## v0.2.0

Hybrid BM25 + Vector search with Reciprocal Rank Fusion.

- **FTS5 BM25 full-text search** — SQLite FTS5 trigram index for keyword matching, Korean whitespace tokenization
- **Reciprocal Rank Fusion (RRF)** — k=60 fusion of BM25 and vector results into a unified ranked list
- **HybridSearcher** — automatic search mode selection (hybrid / bm25_only / vector_only) with graceful degradation
- **Embedding server optional** — BM25 keyword search works without any embedding server configured (NullEmbeddingProvider)
- **Data path migration** — index storage moved from `{contextFolder}/.rag/` to `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{hash}\`; existing indices auto-migrated
- **search_documents enhanced** — returns `search_mode` field indicating which search strategy was used
- **Default threshold lowered** — similarity threshold 0.5 → 0.3 for better recall

## v0.1.0

Initial release.

- **MCP RAG server** — stdio transport, 3 tools (`index_documents`, `search_documents`, `get_document_chunk`)
- **Document parsing** — DOCX, HWPX via FieldCure.DocumentParsers; TXT, MD natively; auto-extends with new parsers
- **Incremental indexing** — SHA256 change detection, orphan cleanup for deleted files
- **Korean-optimized chunking** — regex-based sentence boundary splitting (습니다./해요./하죠.), decimal protection (3.14), parenthesis-aware splitting
- **OpenAI-compatible embeddings** — supports Ollama, LM Studio, OpenAI, Azure OpenAI, Groq, Together AI
- **SQLite vector store** — WAL mode, SIMD-accelerated cosine similarity, MemoryMarshal serialization
- **42 unit tests** — chunking, storage, embedding, document parsing with real test files
