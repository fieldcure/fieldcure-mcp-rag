# Backlog

Post-v1.4.2 improvements that are not yet scheduled. Items here are design
notes, not commitments — promote to a tracked release when ready.

## v1.4.3 (planned)

### Chunker pre-validation against embedding model token limits

The binary-split helper catches oversized-chunk rejections at Stage 4, but a
chunk that exceeds the embedding model's per-input cap can never succeed and
always ends up marked `Failed`. Add a pre-pass in `TextChunker` (or a new
`ChunkTokenValidator`) that re-splits any chunk whose estimated token count
exceeds a per-model ceiling. The ceiling table lives alongside the embedding
provider registry: `text-embedding-3-large = 8192`, `voyage-3-large = 32000`,
`qwen3-embedding:8b = 32768`, etc. Tokenization estimator can be a cheap
heuristic (bytes/4 for English, bytes/2 for Korean) — we don't need to match
the provider exactly, only to stay safely under the limit.

This is the structural fix for the 1999 PDF / chunk 846 scenario.
Binary-split stays in place as a safety net for mis-estimation.

## Indexing summary diagnostics

### Contextualization failure count in per-file log line

Current `index_timing.log` format:

```
[Index] 1999-Introduction to Electrodynamics.pdf — 850 chunks (850 raw), total=1064048ms
```

The `(850 raw)` note is easy to miss and does not explain *why* those chunks
are raw. Kb-3 smoke test on 2026-04-15 surfaced exactly this: the user picked
an uninstalled Ollama contextualizer model, every Stage 3 call 404'd, and the
summary just said `degraded=2` — "raw" vs "failed via split" distinction was
not obvious.

Proposed format when contextualization rawCount > 0:

```
[Index] 1999-Introduction to Electrodynamics.pdf — 850 chunks
        (contextualization failed: 850/850, falling back to raw)
        total=1064048ms
```

Keep the single-line form for the all-succeeded case. Drop down to the
three-line form only when something went wrong, so happy-path logs stay
compact.

### `chunks_contextualized` counter on `file_index`

Currently we track `chunks_raw` + `chunks_pending` but there is no direct
count of how many chunks in a file were successfully contextualized. Add
`chunks_contextualized INTEGER NOT NULL DEFAULT 0` to `file_index`, populated
at the Commit 2a promotion path. This gives operators a direct
per-file "contextualization health" metric without having to scan `chunks`
rows.

Migration: `AddColumnIfMissing` pattern (like the v1.4.0 → v1.4.1 schema
bump). Bump `TargetUserVersion` to 2.

### `get_index_info` tool: expose contextualization stats

Add response fields:

- `total_chunks_contextualized` — sum of `chunks_contextualized` across all
  files
- `files_contextualization_degraded` — count of files where
  `chunks_contextualized < chunks_raw + chunks_contextualized` (i.e. any
  chunks fell back to raw)
- `last_contextualization_failure_rate` — 0.0–1.0, last run only

Host applications can surface a "⚠ 50% contextualization failure" badge in
the Knowledge Bases page without having to run their own SQL.

### `check_changes` tool: `is_contextualization_degraded` flag

Extend the v1.4.1 `is_clean` semantic. Currently `is_clean` folds in
`is_prompt_stale` and `is_schema_stale`. Add a parallel
`is_contextualization_degraded` flag that is true when any file in the KB has
`chunks_raw > 0`.

Do NOT fold this into `is_clean` — a degraded KB is still usable for search,
unlike the stale-schema / stale-prompt cases which require a re-index. The
host UI can decide whether to surface a "partially degraded" warning
independently of the "content/prompt changed" signals.

### Partial re-index preserving OCR output

When only the contextualizer or embedding model changes, OCR output is
structurally unchanged — it is derived from the source document and
nothing else. The AssistStudio Knowledge Bases page currently handles
any model change by deleting `rag.db` and running `exec` from scratch,
which re-runs OCR (20+ minutes for a scanned 600-page PDF) even though
the result would be bit-identical.

Proposal: add an `exec --partial=<stage>` flag to RAG, where
`<stage>` is one of:

- `contextualization` — keep `chunks.content` and `file_index`, clear
  `chunks.enriched` and the `embeddings` table, mark all chunks as a
  new `PendingContextualization` status, and re-run Stages 3–4. Use
  case: user switched contextualizer from `gpt-4o-mini` to
  `claude-haiku-4-6` (or was hit by the kb-3 missing-model scenario).
- `embedding` — keep `chunks.enriched` and `file_index`, clear the
  `embeddings` table, mark all chunks as `PendingEmbedding`, and
  re-run Stage 4 only. Use case: user switched from
  `text-embedding-3-small` (1536d) to `text-embedding-3-large`
  (3072d), or wants to try `qwen3-embedding:8b` without re-OCR.

Schema additions:

- `ChunkIndexStatus.PendingContextualization` enum value
- Engine branch in `IndexingEngine.RunAsync` that respects the new
  flag and skips Stage 1-2 for files whose chunks are already present

AssistStudio-side change: the `modelChanged` branch in
`KnowledgeBasesPage.OnSettingsClicked` replaces its "delete rag.db +
full re-index" path with a per-change-kind call into the new partial
flags. A single-change (contextualizer OR embedding) becomes a
minutes-scale operation instead of a full-OCR-scale one.

Complexity is moderate — the schema migration is small and the
engine loop already persists Commit 1 before Stage 4, so the
infrastructure mostly exists. The tricky bit is making the new
"PendingContextualization" status interact cleanly with the
`ShouldSkipOnHashMatch` helper and the deferred retry pass.

### `unload_kb` MCP tool for deletion unblocking

The AssistStudio Knowledge Bases page cannot cleanly delete a KB while
the serve process is holding a read-only handle to its `rag.db`. The
exec process can be cancelled via the cancel-file path, but serve
caches KB instances inside `MultiKbContext` and keeps the SQLite
connection open across search calls. On Windows this blocks
`Directory.Delete` even for read-only handles.

Current host-side mitigation: `KnowledgeBaseStore.Delete` does a
retry loop (3 seconds total) and surfaces a "files still in use"
dialog if the lock outlasts it. Works for exec-only locks; serve
locks require the user to wait for KB TTL eviction or restart the app.

Proposal: expose an MCP tool on serve — `unload_kb` taking a
`kb_id` — that evicts the cached instance from `MultiKbContext`,
disposes the underlying `SqliteVectorStore`, and confirms release.
AssistStudio calls it right before `KnowledgeBaseStore.Delete` so
the sequence is:

1. UI: user confirms delete
2. Host: cancel exec (existing)
3. Host: invoke `unload_kb` on serve (new)
4. Host: `Directory.Delete` — succeeds on the first attempt
5. Serve: next search for any other KB continues unaffected; a
   subsequent call against the deleted KB returns a clean "KB not
   found" error

The tool is idempotent and side-effect-free on wrong ids, so
hosts can call it unconditionally. Serve's existing lazy-load
path reopens the KB on the next search call, so a "phantom
unload" mid-query is recoverable without restart.

### Counter delta-vs-state semantics cleanup

See `project_counter_semantics.md` in the AssistStudio memory. The in-memory
counters in `IndexingEngine.RunAsync` (`indexed`, `skipped`, `failed`,
`degraded`, `partiallyDeferred`, `needsAction`) mix per-run delta and DB
snapshot semantics without a clear separation. After the v1.4.2 fix that
removed the broken sanity check, the summary line still reports `degraded=0`
in subsequent runs even when the KB has Degraded files from prior runs —
misleading for operators. Pick one semantic (probably "current DB state"
for degraded/failed/partiallyDeferred/needsAction, and "per-run delta" for
indexed/skipped) and enforce consistently.
