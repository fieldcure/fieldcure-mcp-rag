# RAG Indexing Pipeline — Failure Handling & Architecture

> Internal design document for the FieldCure RAG indexing pipeline.
> Covers the exec/SQLite communication model, per-stage failure semantics,
> chunk state machine, and known issues.

---

## 1. Pipeline Overview

The indexing pipeline converts source files into searchable chunks stored in a
per-KB SQLite database. It runs in two modes:

| Mode | Entry point | Purpose |
|------|-------------|---------|
| `exec` | `fieldcure-mcp-rag exec --path {kbPath}` | Headless indexing (spawned as a child process) |
| `serve` | `fieldcure-mcp-rag serve` | MCP server over stdio; reads the index, monitors progress |

### Stage flow (per file)

```
┌─────────┐    ┌─────────┐    ┌──────────┐    ┌────────────────┐    ┌─────────┐    ┌───────┐
│ Collect │───>│  Parse  │───>│  Chunk   │───>│ Contextualize  │───>│  Embed  │───>│ Store │
│ files   │    │ to text │    │ (split)  │    │ (LLM enrich)   │    │ (vector)│    │ (DB)  │
└─────────┘    └─────────┘    └──────────┘    └────────────────┘    └─────────┘    └───────┘
```

All stages for a single file execute inside one `try/catch` block in
`IndexingEngine.RunAsync()`. An exception at **any** stage fails the
**entire file** — there is no partial-file recovery.

### Key source files

| Component | Path |
|-----------|------|
| Pipeline orchestrator | `src/.../Indexing/IndexingEngine.cs` |
| SQLite storage | `src/.../Storage/SqliteVectorStore.cs` |
| Anthropic contextualizer | `src/.../Contextualization/AnthropicChunkContextualizer.cs` |
| OpenAI contextualizer | `src/.../Contextualization/OpenAiChunkContextualizer.cs` |
| Embedding provider | `src/.../Embedding/OpenAiCompatibleEmbeddingProvider.cs` |
| Null providers | `src/.../Embedding/NullEmbeddingProvider.cs`, `src/.../Contextualization/NullChunkContextualizer.cs` |

---

## 2. Exec / SQLite Communication Model

The serve-mode MCP server never indexes directly. Instead, it spawns an `exec`
process and monitors progress through the **shared SQLite database**.

```
┌─────────────────────────┐          ┌─────────────────────┐
│  AssistStudio           │          │  fieldcure-mcp-rag  │
│  (WinUI app)            │          │  exec mode          │
│                         │          │                     │
│  RagProcessManager      │───spawn─>│  IndexingEngine     │
│    .StartExec()         │          │    .RunAsync()      │
│                         │          │                     │
│  KnowledgeBaseStore     │<──read───│  SqliteVectorStore  │
│    .GetIndexingStatus() │          │     (shared rag.db) │
└─────────────────────────┘          └─────────────────────┘
          │                                      │
          │    ┌────────────┐                    │
          └───>│ cancel file│<───────────────────┘
               │ {kb}/cancel│  (checked between files)
               └────────────┘
```

### IPC primitives

There is **no socket, pipe, or RPC** between the two processes. All
communication flows through three SQLite-level mechanisms:

#### 2.1 Indexing lock (`_indexing_lock` table)

```sql
CREATE TABLE _indexing_lock (
    id       INTEGER PRIMARY KEY CHECK (id = 1),  -- singleton
    pid      INTEGER NOT NULL,
    started  TEXT NOT NULL,     -- ISO 8601
    current  INTEGER NOT NULL,  -- files processed so far
    total    INTEGER NOT NULL   -- total files to process
);
```

- **Acquire**: `INSERT OR IGNORE` with current PID. Returns false if row
  exists (another process holds it).
- **Progress**: `UPDATE ... SET current = @n, total = @t` after each file.
- **Release**: `DELETE` on completion or in `finally` block.
- **Stale lock cleanup**: If the PID in the lock row is no longer alive
  (`Process.GetProcesses()` check), the row is auto-deleted before acquire.

#### 2.2 Cancel file

- The UI writes an empty file at `{kbPath}/cancel`.
- The exec process checks `File.Exists(...)` **between files** (not mid-file).
- On detection: release lock, delete cancel file, return exit code 2.

#### 2.3 Result metadata (`index_metadata` table)

After indexing completes, the following keys are persisted:

| Key | Value | Since |
|-----|-------|-------|
| `last_failed_count` | Integer count of failed files | v1.3 |
| `last_failed_files` | JSON array of source paths | v1.3 |
| `last_failed_reasons` | JSON array of exception messages | v1.3 |
| `last_indexed_count` | Integer count of successfully indexed files | v1.4 |
| `last_skipped_count` | Integer count of skipped files | v1.4 |
| `last_degraded_count` | Integer count of degraded files (partial contextualization) | v1.4 |
| `last_partially_deferred_count` | Integer count of files deferred due to embedding failure | v1.4 |
| `last_run_duration_ms` | Integer elapsed milliseconds | v1.4 |
| `last_run_completed_utc` | ISO 8601 timestamp | v1.4 |
| `last_provider_health` | Integer `ProviderHealth` enum value | v1.4 |

The MCP tool `get_index_info` reads these values to populate the KB status
UI (e.g., "3 indexed, 1 failed, 1 degraded").

---

## 3. Stage-by-Stage Failure Handling (v1.4)

Each stage has its own exception type and dedicated catch block in
`IndexingEngine.RunAsync()`. `OperationCanceledException` is **never**
caught — it propagates to the caller for graceful shutdown.

| Stage | Exception type | Counter | Logged | Data impact | `file_index.status` |
|-------|---------------|---------|--------|-------------|---------------------|
| **Extract** | `FileExtractionException` | `failed++` | `LogWarning` + timing log | Previous chunks **preserved** | `NeedsAction` |
| **Chunk** | (empty result) | `skipped++` | — | No change | — |
| **Contextualize** | `EnrichResult.Failed` (non-exception) | `degraded++` | `LogWarning` (per chunk) | Chunk indexed with original text | `Degraded` |
| **Embed** | `EmbeddingException` | `partiallyDeferred++` | `LogWarning` + timing log | Previous chunks **preserved** | `PartiallyDeferred` |
| **Store** | (inner transaction) | `failed++` | `LogError` | Transaction rollback preserves old data | unchanged |
| **Unexpected** | `Exception` | `failed++` | `LogError` | No file_index change | unchanged |

### Contextualization: non-fatal, per-chunk `EnrichResult`

Contextualizer implementations return `EnrichResult.Failed(originalText, ex)`
instead of throwing. This means:

- The chunk is indexed with its original text (`is_contextualized = 0`)
- A `LogWarning` is emitted per failed chunk
- The file is marked `FileIndexStatus.Degraded` with `chunks_raw` count
- The `ProviderHealth` is set to `ContextualizerUnavailable`

### Embedding failure: deferred, not failed

When the embedding provider fails, the file is **not** counted as `failed`.
Instead:

- `partiallyDeferred++` (separate counter)
- `file_index.status = PartiallyDeferred`, `last_error_stage = "embed"`
- Previous chunks remain searchable
- `ProviderHealth = EmbeddingUnavailable` (visible in `_indexing_lock`)

### `IndexingResult` return type

`RunAsync` returns `IndexingResult` with structured counters:

| Field | Meaning |
|-------|---------|
| `Indexed` | Files fully indexed (includes degraded) |
| `Skipped` | Unchanged hash / empty text / zero chunks |
| `Failed` | Extract failures + unexpected errors |
| `Degraded` | Indexed but with some raw (non-contextualized) chunks |
| `PartiallyDeferred` | Embedding failed; previous data preserved |
| `ExitCode` | 0 = success (incl. degraded/deferred), 1 = all failed, 2 = cancelled |

### Empty-text and empty-chunks cases

These are **not** failures:

| Condition | Result | Counter |
|-----------|--------|---------|
| `ParseDocumentAsync` returns empty/whitespace | `skipped++` | Not `failed` |
| `TextChunker.Split` returns 0 chunks | `skipped++` | Not `failed` |
| File hash unchanged (no `--force`) | `skipped++` | Not `failed` |

---

## 4. Data Loss Bug (Fixed)

### Before (vulnerable)

```
DeleteBySourcePathAsync(storagePath)   // ← old data gone
  ↓
TextChunker.Split(text)
  ↓
Contextualizer.EnrichAsync(...)
  ↓
EmbeddingProvider.EmbedBatchAsync(...)  // ← if this throws...
  ↓                                     //   old data deleted, new data never stored
UpsertChunkAsync(...)                   //   = file unsearchable until next reindex
```

### After (atomic)

```
TextChunker.Split(text)
  ↓
Contextualizer.EnrichAsync(...)
  ↓
EmbeddingProvider.EmbedBatchAsync(...)  // ← if this throws...
  ↓                                     //   old data preserved (ReplaceFileChunksAsync never called)
ReplaceFileChunksAsync(...)             // ← single SQLite transaction:
  BEGIN                                 //     DELETE old + INSERT new
  DELETE FROM chunks WHERE source_path = ...
  INSERT INTO chunks ...  (× N)
  INSERT INTO embeddings ... (× N)
  DELETE FROM chunks_fts WHERE ...
  INSERT INTO chunks_fts ... (× N)
  COMMIT                                //   on failure → ROLLBACK, old data intact
```

### Guard: empty chunks

`ReplaceFileChunksAsync` returns early when `chunks.Count == 0`, preserving
existing data. The caller (`IndexingEngine`) also skips the call when
chunking produces zero results (`skipped++`).

---

## 5. Chunk State Machine

A chunk's effective state is determined by the combination of the `enriched`
column in the `chunks` table and the presence of a row in the `embeddings`
table.

```
                    ┌─────────────────────┐
                    │     File parsed      │
                    │   & chunked          │
                    └──────────┬──────────┘
                               │
                    ┌──────────▼──────────┐
                    │  Contextualization   │
                    │  configured?         │
                    └──┬──────────────┬───┘
                   yes │              │ no (NullChunkContextualizer)
                       │              │
               ┌───────▼──────┐  ┌────▼────────────┐
               │ LLM call     │  │ enriched=content │
               │ succeeds?    │  │ (passthrough)    │
               └──┬───────┬──┘  └────┬────────────┘
              yes │       │ no       │
                  │       │          │
          ┌───────▼──┐ ┌──▼────────┐ │
          │ Enriched  │ │ Degraded  │ │
          │ enriched  │ │ enriched  │ │
          │ ≠ content │ │ = content │ │
          └─────┬─────┘ └─────┬────┘ │
                │             │       │
                └──────┬──────┘───────┘
                       │
                ┌──────▼──────────────┐
                │  Embedding          │
                │  configured?        │
                └──┬──────────────┬──┘
               yes │              │ no (NullEmbeddingProvider)
                   │              │
           ┌───────▼──────┐  ┌───▼───────────┐
           │  Searchable   │  │  BM25-only    │
           │  (vector+FTS) │  │  (FTS only)   │
           │  model=X      │  │  no embedding │
           └───────────────┘  └───────────────┘
```

### State definitions

| State | `enriched` | `embeddings` row | Search capability | How it happens |
|-------|-----------|-----------------|-------------------|----------------|
| **Searchable** | content or enriched text | Present (model=X) | Vector + BM25 hybrid | Normal indexing path |
| **Enriched** | LLM-generated context | Present | Vector uses enriched text | Contextualizer succeeded |
| **Degraded** | = `content` (original) | Present | Vector uses original text | Contextualizer failed silently |
| **BM25-only** | content or enriched | **Absent** | BM25 full-text only | `NullEmbeddingProvider` configured |
| **Stale** | any | Present (model=Y, Y≠current) | Vector search works but quality may be poor | Embedding model changed in config |

### Detection (v1.5)

Since v1.4, chunk state is tracked explicitly via the `status` and
`is_contextualized` columns in the `chunks` table:

- **Degraded**: `chunks.is_contextualized = 0` — no longer requires
  inference from `enriched = content`.
- **PendingEmbedding**: `chunks.status = 2` — queryable via
  `GetPendingEmbeddingChunksAsync()` for deferred retry.
- **PendingContextualization**: `chunks.status = 5` (v1.5) — set by
  `--partial=contextualization`, processed by `RunPartialContextualizationPassAsync`.
- **Stale**: Compare `embeddings.model` against `config.Embedding.Model`.
  Addressed in v1.5 via `--partial=embedding` which re-embeds all chunks.

---

## 6. Resolved Issues (v1.4)

### ~~Silent contextualization failures~~

**Resolved.** Contextualizers now return `EnrichResult.Failed(...)` with
`FailureReason` and `FailureType`. A `LogWarning` is emitted per failed
chunk. The `is_contextualized` column and `FileIndexStatus.Degraded` make
degraded state visible in the database and `get_index_info` response.

### ~~Single failure counter~~

**Resolved.** `IndexingResult` now has separate `Failed`, `Degraded`, and
`PartiallyDeferred` counters. `file_index.last_error_stage` records the
pipeline stage where the error occurred.

### ~~Data loss on embedding failure~~

**Resolved.** `ReplaceFileChunksAsync` wraps delete + insert in a single
SQLite transaction. Embedding failures are caught before the transaction
and leave previous data intact via `MarkFileAsFailedAsync`.

## 7. Resolved Issues (v1.5)

### ~~No retry logic within a single run~~

**Resolved in v1.4.2.** `RunDeferredRetryPassAsync` automatically retries
`PendingEmbedding` chunks at the end of every exec run.

### ~~Stale embedding detection~~

**Resolved in v1.5.0.** `--partial=embedding` re-embeds all chunks when
the embedding model changes, without re-running OCR. AssistStudio's
Settings page automatically selects the correct partial flag based on
which model changed.
