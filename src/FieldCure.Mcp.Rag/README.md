# FieldCure.Mcp.Rag

> **Windows only** — credential storage uses Windows Credential Manager (`advapi32.dll`). Cross-platform support is planned via a shared credential abstraction package.

**MCP RAG server with hybrid BM25 + vector search and AI-powered chunk contextualization** — indexes documents from configured source paths, enriches chunks with AI-generated context and keywords, generates embeddings via OpenAI-compatible APIs, and performs keyword (FTS5) and semantic (cosine similarity) search with Reciprocal Rank Fusion.

## Install

```bash
dotnet tool install -g FieldCure.Mcp.Rag
```

## Architecture

```
fieldcure-mcp-rag
├── exec  --path <kb-path> [--force] [--partial contextualization|embedding]
└── serve --base-path <path>           # Multi-KB MCP search server (stdio)
```

- **exec** — headless indexing process. Scans source paths, chunks, contextualizes, embeds, stores. Uses a 2-commit model that persists expensive upstream work (OCR, contextualization) before Stage 4 so embedding failures never lose data, plus binary-split per-chunk failure isolation and an automatic deferred retry pass. Supports `--partial` mode to re-run only downstream stages when models change, preserving OCR output.
- **serve** — read-only MCP server serving all KBs under the base path. Tools accept `kb_id` parameter to target a specific KB. Lazy-loads KB instances on first access.

Both modes read `config.json` from the knowledge base folder and resolve API keys from Windows PasswordVault.

## Quick Start

### 1. Create a knowledge base

Create `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{kb-id}\config.json`:

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

### 2. Index

```bash
fieldcure-mcp-rag exec --path "%LOCALAPPDATA%\FieldCure\Mcp.Rag\my-kb-001"
```

### 3. Search (MCP server)

```bash
fieldcure-mcp-rag serve --base-path "%LOCALAPPDATA%\FieldCure\Mcp.Rag"
```

A single serve process handles all knowledge bases under the base path.

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

## Tools (6)

All tools (except `list_knowledge_bases`) require a `kb_id` parameter.

| Tool | Description |
|------|-------------|
| `list_knowledge_bases` | List all available KBs with status (file/chunk counts, indexing status) |
| `search_documents` | Hybrid BM25 + vector search with RRF. Supports `search_mode`: `auto`, `bm25`, `vector` |
| `get_document_chunk` | Retrieve full content of a specific chunk by ID |
| `get_index_info` | Index metadata including contextualization health stats. Internal |
| `check_changes` | Dry-run filesystem scan with `is_contextualization_degraded` flag. Internal |
| `unload_kb` | Release SQLite handles for a KB before deletion. Internal |

## Search Modes

The `search_mode` parameter controls the search strategy:

| `search_mode` | Behavior |
|---------------|----------|
| `auto` (default) | Hybrid when embedding is available, else BM25. Recommended |
| `bm25` | Keyword-only (FTS5). No embedding call. Use for lightweight consumers |
| `vector` | Semantic-only. Requires embedding provider; errors if unavailable |

When `search_mode` is `auto`, the actual mode depends on the provider and query:

| Actual mode | When | Description |
|-------------|------|-------------|
| `hybrid` | Embedding configured + BM25 results exist | BM25 + vector, fused via RRF |
| `bm25_only` | No embedding configured | FTS5 trigram keyword search only |
| `vector_only` | BM25 returns no results | Cosine similarity search only |

Embedding is optional. Without it, BM25 keyword search still works.

## Supported Formats

DOCX, HWPX, XLSX, PPTX, PDF (with OCR fallback for scanned pages), TXT, MD — auto-extends when new parsers are added to FieldCure.DocumentParsers.

## config.json Reference

| Field | Description |
|-------|-------------|
| `id` | Knowledge base identifier |
| `name` | Display name |
| `sourcePaths` | Folders to index (multiple supported) |
| `contextualizer.provider` | `"anthropic"`, `"openai"`, `"ollama"`, or empty to disable |
| `contextualizer.model` | Model ID, or empty to disable contextualization |
| `contextualizer.apiKeyPreset` | PasswordVault preset name (e.g., `"Claude"`, `"OpenAI"`) |
| `contextualizer.baseUrl` | API base URL override (null = provider default) |
| `embedding.*` | Same structure as contextualizer |
| `embedding.maxChunkChars` | Max chars per chunk before pre-split (default: 4000) |
| `embedding.batchSize` | Max chunks per embedding API call (default: auto from table) |
| `systemPrompt` | Custom system prompt (null = built-in default) |

## Data Storage

Knowledge base data at `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{kb-id}\`:
- `config.json` — knowledge base configuration (created by app)
- `rag.db` — SQLite database (chunks, embeddings, FTS5 index, file hashes, indexing lock)

## Requirements

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or later

## See Also

Part of the [AssistStudio ecosystem](https://github.com/fieldcure/fieldcure-assiststudio#packages).

## Links

- [GitHub](https://github.com/fieldcure/fieldcure-mcp-rag)
- [MCP Specification](https://modelcontextprotocol.io)
