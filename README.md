# FieldCure MCP RAG Server

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that indexes documents and performs hybrid BM25 + vector search with Reciprocal Rank Fusion. Built with C# and the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Features

- **PDF indexing** — `.pdf` files indexed and searchable with page-by-page text extraction (v0.10.0)
- **Math equation indexing** — DOCX/HWPX math equations extracted as `[math: LaTeX]` blocks and searchable (v0.10.0)
- **Cross-process indexing lock** — SQLite-based mutex prevents concurrent indexing; stale PID auto-cleanup (v0.9.0)
- **Indexing progress** — MCP `notifications/progress` during indexing for real-time progress bar display (v0.8.0)
- **Chunk Contextualization** — AI-powered context + keyword enrichment per chunk for improved search (v0.3.0)
- **Hybrid search** — BM25 keyword (FTS5) + semantic vector search, fused via Reciprocal Rank Fusion (RRF)
- **Embedding optional** — BM25 keyword search works without any embedding server configured
- **4 MCP tools** — index documents, hybrid search, chunk retrieval, index info
- **Incremental indexing** — SHA256 change detection, only re-indexes modified files
- **Orphan cleanup** — automatically removes DB entries for deleted files
- **Korean-optimized chunking** — sentence boundary splitting for Korean, decimal protection, parenthesis-aware
- **OpenAI-compatible embeddings** — works with Ollama, LM Studio, OpenAI, Azure OpenAI, Groq, Together AI
- **SIMD-accelerated search** — cosine similarity via `System.Numerics.Vector`
- **SQLite storage** — WAL mode, FTS5 trigram index, single-file database, zero configuration
- **Stdio transport** — standard MCP subprocess model via JSON-RPC over stdin/stdout

## Installation

### dotnet tool (recommended)

```bash
dotnet tool install -g FieldCure.Mcp.Rag
```

### From source

```bash
git clone https://github.com/fieldcure/fieldcure-mcp-rag.git
cd fieldcure-mcp-rag
dotnet build
```

## Requirements

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- An embedding provider (Ollama, OpenAI, etc.) — optional, BM25 search works without it

## Configuration

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "rag": {
      "command": "fieldcure-mcp-rag",
      "args": ["C:\\Users\\me\\Documents\\RagContext"],
      "env": {
        "EMBEDDING_BASE_URL": "http://localhost:11434",
        "EMBEDDING_API_KEY": "",
        "EMBEDDING_MODEL": "nomic-embed-text"
      }
    }
  }
}
```

### VS Code (Copilot)

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "rag": {
      "command": "fieldcure-mcp-rag",
      "args": ["${workspaceFolder}"],
      "env": {
        "EMBEDDING_BASE_URL": "http://localhost:11434",
        "EMBEDDING_MODEL": "nomic-embed-text"
      }
    }
  }
}
```

### Environment Variables

#### Embedding

| Variable | Default | Description |
|----------|---------|-------------|
| `EMBEDDING_BASE_URL` | `http://localhost:11434` | Embedding API base URL |
| `EMBEDDING_API_KEY` | *(empty)* | API key (empty for local servers) |
| `EMBEDDING_MODEL` | `nomic-embed-text` | Model identifier |
| `EMBEDDING_DIMENSION` | `0` (auto-detect) | Vector dimension |

#### Chunk Contextualization (v0.3.0)

| Variable | Default | Description |
|----------|---------|-------------|
| `CONTEXTUALIZER_PROVIDER` | `openai` | Provider: `openai` or `anthropic` |
| `CONTEXTUALIZER_BASE_URL` | *(empty)* | API base URL (e.g., `http://localhost:11434` for Ollama) |
| `CONTEXTUALIZER_API_KEY` | *(empty)* | API key (empty for local servers) |
| `CONTEXTUALIZER_MODEL` | *(empty)* | Model identifier. If empty, contextualization is disabled (v0.2.0 behavior) |

When configured, the contextualizer enriches each chunk with AI-generated context descriptions and normalized keywords during indexing. Search uses the enriched text; responses return the original text.

## Tools

| Tool | Description |
|------|-------------|
| `index_documents` | Index all supported documents (incremental). Reports progress via MCP notifications |
| `search_documents` | Hybrid BM25 + vector search with RRF fusion |
| `get_document_chunk` | Retrieve full content of a specific chunk by ID |
| `get_index_info` | Index metadata (file/chunk counts, prompt config, stale detection, indexing lock status) |

### Search Modes

| Mode | When | Description |
|------|------|-------------|
| `hybrid` | Embedding server + query >= 3 chars | BM25 keyword + vector semantic, fused via RRF |
| `bm25_only` | No embedding server configured | FTS5 trigram keyword search only |
| `vector_only` | Query tokens all < 3 chars | Cosine similarity search only |

### Supported Formats

Document formats are provided by [FieldCure.DocumentParsers](https://github.com/fieldcure/fieldcure-document-parsers):

- **DOCX** — Microsoft Word (with math equation extraction)
- **HWPX** — Korean standard document (OWPML, with math equation extraction)
- **XLSX** — Excel spreadsheets (v0.3.0)
- **PPTX** — PowerPoint presentations (v0.3.0)
- **PDF** — PDF text extraction with `## Page N` headers (v0.10.0)
- **TXT, MD** — Plain text / Markdown

Additional formats are automatically supported when new parsers are registered.

## Architecture

```
src/FieldCure.Mcp.Rag/
├── Program.cs                  # Entry point, DI setup, stdio transport
├── RagContext.cs               # DI service container
├── Contextualization/
│   ├── IChunkContextualizer.cs         # Contextualizer abstraction
│   ├── NullChunkContextualizer.cs      # No-op (default, v0.2.0 behavior)
│   ├── ChunkContextualizerHelper.cs    # Shared prompt/parsing logic
│   ├── OpenAiChunkContextualizer.cs    # OpenAI/Ollama/Groq
│   └── AnthropicChunkContextualizer.cs # Claude API (/v1/messages)
├── Embedding/
│   ├── IEmbeddingProvider.cs   # Embedding abstraction
│   ├── OpenAiCompatibleEmbeddingProvider.cs
│   ├── NullEmbeddingProvider.cs
│   └── EmbeddingProviderFactory.cs
├── Storage/
│   └── SqliteVectorStore.cs    # SQLite + FTS5 BM25 + SIMD cosine similarity
├── Search/
│   ├── HybridSearcher.cs       # BM25 + Vector → RRF orchestration
│   └── RrfFusion.cs            # Reciprocal Rank Fusion (k=60)
├── Chunking/
│   └── TextChunker.cs          # Korean/English sentence-aware chunking
├── Tools/
│   ├── IndexDocumentsTool.cs   # Incremental indexing with orphan cleanup
│   ├── SearchDocumentsTool.cs  # Hybrid search with mode selection
│   └── GetDocumentChunkTool.cs # Chunk retrieval
└── Models/
    ├── DocumentChunk.cs
    ├── SearchResult.cs
    ├── HybridSearchResult.cs   # Results + SearchMode + metadata
    └── SearchMode.cs           # Hybrid / VectorOnly / Bm25Only
```

## Data Storage

Index data is stored at `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{folder_hash}\`:
- `rag_index.db` — SQLite database (chunks, embeddings, FTS5 index, file hashes, indexing lock)

Existing v0.1.0 indices at `{contextFolder}/.rag/` are auto-migrated on first run.

## Development

```bash
# Build
dotnet build

# Test
dotnet test

# Pack as dotnet tool
dotnet pack src/FieldCure.Mcp.Rag -c Release
```

## License

[MIT](LICENSE)
