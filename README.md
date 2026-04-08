# FieldCure MCP RAG Server

[![NuGet](https://img.shields.io/nuget/v/FieldCure.Mcp.Rag)](https://www.nuget.org/packages/FieldCure.Mcp.Rag)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/fieldcure/fieldcure-mcp-rag/blob/main/LICENSE)

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that indexes documents and performs hybrid BM25 + vector search with Reciprocal Rank Fusion. Built with C# and the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Architecture

```
fieldcure-mcp-rag
├── exec  --path <kb-path> [--force]   # Headless indexing (single KB)
└── serve --base-path <path>           # Multi-KB MCP search server (stdio)
```

- **exec** — scans source folders, chunks documents, contextualizes with AI, embeds, stores in SQLite. Runs as a detached process.
- **serve** — read-only MCP server serving all knowledge bases under the base path. Single process handles multiple KBs via `kb_id` parameter. Can run while exec is indexing (SQLite WAL).

## Features

- **Multi-KB serve mode** — single MCP server process serves all knowledge bases under a base path, lazy-loaded per KB
- **exec/serve dual mode** — headless indexing + search-only MCP server
- **PasswordVault credentials** — API keys resolved from Windows Credential Manager, shared with AssistStudio
- **Per-KB config.json** — source paths, model settings, credential presets per knowledge base
- **Cancel file** — graceful exec shutdown via `{kb-path}/cancel` file
- **PDF indexing** — `.pdf` files indexed and searchable with page-by-page text extraction; scanned PDFs supported via Tesseract OCR fallback (English + Korean)
- **Math equation indexing** — DOCX/HWPX math equations extracted as `[math: LaTeX]` blocks and searchable
- **Cross-process indexing lock** — SQLite-based mutex prevents concurrent indexing; stale PID auto-cleanup
- **Chunk contextualization** — AI-powered context + keyword enrichment per chunk for improved search
- **Hybrid search** — BM25 keyword (FTS5) + semantic vector search, fused via Reciprocal Rank Fusion (RRF)
- **Embedding optional** — BM25 keyword search works without any embedding server configured
- **4 MCP tools** — KB listing, hybrid search, chunk retrieval, index info
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

## Usage

### 1. Create a knowledge base folder

```
%LOCALAPPDATA%\FieldCure\Mcp.Rag\{kb-id}\config.json
```

```json
{
  "id": "my-kb-001",
  "name": "Project Docs",
  "created": "2026-04-03T00:00:00Z",
  "sourcePaths": ["C:\\Users\\me\\Documents\\project-docs"],
  "contextualizer": {
    "provider": "anthropic",
    "model": "claude-haiku-4-5-20251001",
    "apiKeyPreset": "Claude"
  },
  "embedding": {
    "provider": "openai",
    "model": "text-embedding-3-small",
    "apiKeyPreset": "OpenAI"
  }
}
```

### 2. Index documents

```bash
fieldcure-mcp-rag exec --path "C:\Users\me\AppData\Local\FieldCure\Mcp.Rag\my-kb-001"
```

### 3. Start MCP search server

```bash
fieldcure-mcp-rag serve --base-path "C:\Users\me\AppData\Local\FieldCure\Mcp.Rag"
```

A single serve process handles all knowledge bases under the base path. Tools accept a `kb_id` parameter to target a specific KB.

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "rag": {
      "command": "fieldcure-mcp-rag",
      "args": ["serve", "--base-path", "C:\\Users\\me\\AppData\\Local\\FieldCure\\Mcp.Rag"]
    }
  }
}
```

### config.json Reference

| Field | Description |
|-------|-------------|
| `id` | Knowledge base identifier (UUID) |
| `name` | Display name |
| `sourcePaths` | List of folders to index (multiple supported) |
| `contextualizer.provider` | `"anthropic"`, `"openai"`, `"ollama"`, or empty to disable |
| `contextualizer.model` | Model ID, or empty to disable contextualization |
| `contextualizer.apiKeyPreset` | PasswordVault preset name (e.g., `"Claude"`) |
| `contextualizer.baseUrl` | API base URL override (null = provider default) |
| `embedding.*` | Same structure as contextualizer |
| `systemPrompt` | Custom system prompt for contextualization (null = built-in default) |

## Tools

All tools (except `list_knowledge_bases`) require a `kb_id` parameter to specify the target knowledge base.

| Tool | Description |
|------|-------------|
| `list_knowledge_bases` | List all available KBs with status (file/chunk counts, indexing status) |
| `search_documents` | Hybrid BM25 + vector search with RRF fusion |
| `get_document_chunk` | Retrieve full content of a specific chunk by ID |
| `get_index_info` | Index metadata (file/chunk counts, last indexed timestamp, prompt config, stale detection, indexing lock status). Internal — for host application use |
| `check_changes` | Dry-run filesystem scan comparing source files against the index. Returns added/modified/deleted/failed file paths and counts. Internal — for host application use |

### Search Modes

| Mode | When | Description |
|------|------|-------------|
| `hybrid` | Embedding configured + query >= 3 chars | BM25 keyword + vector semantic, fused via RRF |
| `bm25_only` | No embedding configured | FTS5 trigram keyword search only |
| `vector_only` | Query tokens all < 3 chars | Cosine similarity search only |

### Supported Formats

Document formats are provided by [FieldCure.DocumentParsers](https://github.com/fieldcure/fieldcure-document-parsers):

- **DOCX** — Microsoft Word (with math equation extraction)
- **HWPX** — Korean standard document (OWPML, with math equation extraction)
- **XLSX** — Excel spreadsheets
- **PPTX** — PowerPoint presentations
- **PDF** — PDF text extraction with `## Page N` headers; OCR fallback for scanned pages (Tesseract, eng+kor)
- **TXT, MD** — Plain text / Markdown

Additional formats are automatically supported when new parsers are registered.

## Architecture

```
src/FieldCure.Mcp.Rag/
├── Program.cs                  # CLI entry (exec | serve)
├── MultiKbContext.cs           # Multi-KB manager (lazy load, cache, cleanup)
├── Configuration/
│   └── RagConfig.cs            # config.json model
├── Credentials/
│   ├── ICredentialService.cs   # PasswordVault abstraction
│   └── CredentialService.cs    # Windows Credential Manager P/Invoke
├── Indexing/
│   └── IndexingEngine.cs       # Headless indexing pipeline
├── Contextualization/
│   ├── IChunkContextualizer.cs
│   ├── NullChunkContextualizer.cs
│   ├── ChunkContextualizerHelper.cs
│   ├── OpenAiChunkContextualizer.cs
│   └── AnthropicChunkContextualizer.cs
├── Embedding/
│   ├── IEmbeddingProvider.cs
│   ├── OpenAiCompatibleEmbeddingProvider.cs
│   └── NullEmbeddingProvider.cs
├── Storage/
│   └── SqliteVectorStore.cs    # SQLite + FTS5 BM25 + SIMD cosine similarity
├── Search/
│   ├── HybridSearcher.cs       # BM25 + Vector → RRF orchestration
│   └── RrfFusion.cs            # Reciprocal Rank Fusion (k=60)
├── Chunking/
│   └── TextChunker.cs          # Korean/English sentence-aware chunking
├── Tools/
│   ├── ListKnowledgeBasesTool.cs # KB listing
│   ├── SearchDocumentsTool.cs    # Hybrid search with mode selection
│   ├── GetDocumentChunkTool.cs   # Chunk retrieval
│   ├── GetIndexInfoTool.cs       # Index metadata
│   └── CheckChangesTool.cs      # Dry-run file change detection
└── Models/
    ├── DocumentChunk.cs
    ├── SearchResult.cs
    ├── HybridSearchResult.cs
    └── SearchMode.cs
```

## Data Storage

Knowledge base data is stored at `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{kb-id}\`:
- `config.json` — knowledge base configuration
- `rag.db` — SQLite database (chunks, embeddings, FTS5 index, file hashes, indexing lock)

## Development

```bash
# Build
dotnet build

# Test
dotnet test

# Pack as dotnet tool
dotnet pack src/FieldCure.Mcp.Rag -c Release
```

## See Also

Part of the [AssistStudio ecosystem](https://github.com/fieldcure/fieldcure-assiststudio#packages).

## License

[MIT](LICENSE)
