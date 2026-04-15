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
