# Release Notes

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
