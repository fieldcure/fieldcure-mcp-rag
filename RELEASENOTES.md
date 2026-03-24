# Release Notes

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
