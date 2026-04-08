# Release Notes

## v1.1.0 (2026-04-08)

### Added

- **`check_changes` tool** — dry-run filesystem scan that compares source files against the index; returns added/modified/deleted file counts and paths with `is_prompt_stale` and `is_clean` flags
- **`last_indexed_at` in `get_index_info`** — most recent indexing timestamp (ISO 8601 UTC) from `file_index`

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
