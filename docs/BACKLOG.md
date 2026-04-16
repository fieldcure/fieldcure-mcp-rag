# Backlog

Post-v1.5.0 improvements that are not yet scheduled. Items here are design
notes, not commitments — promote to a tracked release when ready.

## Completed in v1.5.0

All items from the original v1.4.3 backlog have been implemented:

- ~~Chunker pre-validation~~ — `TextChunker` enforces `maxChars` (default 4000)
- ~~Contextualization diagnostics~~ — multi-line log, `chunks_contextualized` column, `get_index_info` + `check_changes` stats
- ~~Partial re-index~~ — `--partial=contextualization|embedding` with cancel-resume and cross-kind safety
- ~~`unload_kb` tool~~ — clean KB deletion without retry loops
- ~~Embedding batch size~~ — static table + config override, sequential sub-batching
- ~~Counter delta-vs-state cleanup~~ — `GetStateCountersAsync`, two-line summary format

## Future

### Persistent adaptive batch sizing

The v1.5.0 static table approach (option A+C from the original design) covers
known models. Option B (on first rejection, persist the learned batch size in
`index_metadata`) would handle unknown models automatically. Deferred because
the binary-split fallback already covers this case and the static table
eliminates the common scenarios.

### Bilingual keyword prompt enhancement

phi4-mini and small Ollama models sometimes fail to produce bilingual keywords.
The contextualization prompt may need model-specific tuning or a fallback
strategy for models that can't reliably follow bilingual instructions.
