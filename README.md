# FieldCure MCP RAG Server

[![NuGet](https://img.shields.io/nuget/v/FieldCure.Mcp.Rag)](https://www.nuget.org/packages/FieldCure.Mcp.Rag)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/fieldcure/fieldcure-mcp-rag/blob/main/LICENSE)

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server for indexing and searching local document collections. Supports DOCX, HWPX, PDF (with OCR), Excel, and PowerPoint, with hybrid keyword + semantic search optimized for Korean and English.

Built with C# and the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Two Commands

```
fieldcure-mcp-rag
├── exec  --path <kb-path> [--force]   # Headless indexing (single KB)
└── serve --base-path <path>           # Multi-KB MCP search server (stdio)
```

- **exec** — scans source folders, chunks documents, contextualizes with AI, embeds, stores in SQLite. Runs as a detached process.
- **serve** — read-only MCP server serving all knowledge bases under the base path. Single process handles multiple KBs via `kb_id` parameter. Can run while exec is indexing (SQLite WAL).

## Features

### Search
- Hybrid BM25 + vector search with Reciprocal Rank Fusion (RRF)
- BM25-only fallback when no embedding provider is configured
- Korean-optimized chunking (sentence boundary, decimal protection, parenthesis-aware)
- SIMD-accelerated cosine similarity via `System.Numerics.Vector`
- FTS5 trigram index for substring and CJK-friendly keyword matching

### Indexing
- Incremental indexing with SHA256 change detection
- AI-powered chunk contextualization with bilingual keyword enrichment (see [Chunk Contextualization](#chunk-contextualization))
- Math equation extraction from DOCX/HWPX as `[math: LaTeX]` blocks
- PDF with OCR fallback (Tesseract eng+kor) for scanned pages
- Cross-process indexing lock with stale PID auto-cleanup
- Orphan cleanup for deleted files

### Operations
- exec/serve dual mode — headless indexing + read-only search server
- Multi-KB serve: single process serves all knowledge bases under a base path, lazy-loaded per KB
- SQLite WAL mode allows search during indexing
- Graceful shutdown via `cancel` file
- Per-KB `config.json` with credential presets

### Integration
- OpenAI-compatible embeddings — works with Ollama, LM Studio, OpenAI, Azure OpenAI, Groq, Together AI
- Windows Credential Manager (PasswordVault) for API keys, shared with AssistStudio
- Standard MCP stdio transport (JSON-RPC over stdin/stdout)

## Chunk Contextualization

Standard RAG chunking loses context — a sentence about "the protocol" becomes ambiguous when ripped from its surrounding paragraphs. This server addresses that with **Unified Chunk Contextualization**: a single LLM call per chunk that produces both contextual framing and bilingual (Korean + English) keywords in one pass.

The result is stored alongside the original chunk text:

- **Original text** is preserved for accurate retrieval display
- **Contextualized text** is what gets embedded and indexed in BM25
- **Bilingual keywords** enable cross-lingual search — a Korean query can retrieve English documents and vice versa

This is enabled by setting `contextualizer` in `config.json`. It can be disabled (set provider/model to empty) if you prefer raw chunk indexing.

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
- **Windows x64 only** — Tesseract OCR native binaries are bundled for x64
- An embedding provider (Ollama, OpenAI, etc.) — optional, BM25 search works without it

## Quick Start

Index a folder and search it without any embedding setup (BM25 only):

```powershell
# 1. Install
dotnet tool install -g FieldCure.Mcp.Rag

# 2. Create a minimal config
$kbPath = "$env:LOCALAPPDATA\FieldCure\Mcp.Rag\demo"
New-Item -ItemType Directory -Force -Path $kbPath
@'
{
  "id": "demo",
  "name": "Demo KB",
  "sourcePaths": ["C:\\my-docs"]
}
'@ | Set-Content "$kbPath\config.json"

# 3. Index
fieldcure-mcp-rag exec --path $kbPath

# 4. Start the search server
fieldcure-mcp-rag serve --base-path "$env:LOCALAPPDATA\FieldCure\Mcp.Rag"
```

For full retrieval quality with semantic search and contextualization, add `embedding` and `contextualizer` blocks to `config.json` — see [Usage](#usage) below.

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
| `id` | Knowledge base identifier |
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
| `get_index_info` | Index metadata (file/chunk counts, last indexed timestamp, prompt config, stale detection, indexing lock status). *Primarily used by host applications such as AssistStudio to display KB status.* |
| `check_changes` | Dry-run filesystem scan comparing source files against the index. Returns added/modified/deleted/failed file paths and counts. *Primarily used by host applications to surface re-indexing prompts.* |

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

## Project Structure

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
