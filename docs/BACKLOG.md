# Backlog

Post-v2.0.0 improvements that are not yet scheduled. Items here are design
notes, not commitments — promote to a tracked release when ready.

## Completed in v2.0.0

- Queue-based indexing orchestrator (`start_reindex`, `cancel_reindex`, `exec-queue`)
- `prune-orphans` CLI for orphan KB folder cleanup
- Ollama native providers (`/api/embed`, `/api/chat`) with `keep_alive` and `num_ctx`
- Cross-platform: `CredentialService` removed, API keys via environment variables
- `unload_kb` removed (lazy eviction in `GetKb`)

## Completed in v1.5.0

- Chunker pre-validation — `TextChunker` enforces `maxChars` (default 4000)
- Contextualization diagnostics — multi-line log, `chunks_contextualized` column, stats
- Partial re-index — `--partial=contextualization|embedding` with cancel-resume
- Embedding batch size — static table + config override, sequential sub-batching
- Counter delta-vs-state cleanup — `GetStateCountersAsync`, two-line summary format

## Future

### Persistent adaptive batch sizing

The v1.5.0 static table approach covers known models. Learning the batch size
on first rejection and persisting it in `index_metadata` would handle unknown
models automatically. Deferred because the binary-split fallback already covers
this case.

### Bilingual keyword prompt enhancement

phi4-mini and small Ollama models sometimes fail to produce bilingual keywords.
The contextualization prompt may need model-specific tuning or a fallback
strategy for models that can't reliably follow bilingual instructions.

### Model-specific num_ctx auto-detection

`ollama show <model>` can report the model's maximum context window. Currently
`num_ctx` defaults to 8192 and users override manually. Auto-detection would
eliminate misconfiguration.
