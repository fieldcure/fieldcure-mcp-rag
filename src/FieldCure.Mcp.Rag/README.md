# FieldCure.Mcp.Rag

**MCP RAG server with hybrid BM25 + vector search and AI-powered chunk contextualization** — indexes documents from configured source paths, enriches chunks with AI-generated context and keywords, generates embeddings via OpenAI-compatible APIs, and performs keyword (FTS5) and semantic (cosine similarity) search with Reciprocal Rank Fusion.

## Install

```bash
dotnet tool install -g FieldCure.Mcp.Rag
```

## Architecture (v0.11.0)

```
fieldcure-mcp-rag
├── exec  --path <kb-path> [--force]   # Headless indexing
└── serve --path <kb-path>             # MCP search server (stdio)
```

- **exec** — headless indexing process. Scans source paths, chunks, contextualizes, embeds, stores.
- **serve** — read-only MCP server. Exposes search tools via stdio transport.

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
fieldcure-mcp-rag serve --path "%LOCALAPPDATA%\FieldCure\Mcp.Rag\my-kb-001"
```

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "rag": {
      "command": "fieldcure-mcp-rag",
      "args": ["serve", "--path", "C:\\Users\\me\\AppData\\Local\\FieldCure\\Mcp.Rag\\my-kb-001"]
    }
  }
}
```

## Tools (3)

| Tool | Description |
|------|-------------|
| `search_documents` | Hybrid BM25 + vector search with Reciprocal Rank Fusion |
| `get_document_chunk` | Retrieve full content of a specific chunk by ID |
| `get_index_info` | Returns index metadata (file/chunk counts, prompt config, stale-index detection, indexing lock status). Internal — for host application use |

## Search Modes

| Mode | When | Description |
|------|------|-------------|
| `hybrid` | Embedding configured + query >= 3 chars | BM25 keyword + vector semantic, fused via RRF |
| `bm25_only` | No embedding configured | FTS5 trigram keyword search only |
| `vector_only` | Query tokens all < 3 chars | Cosine similarity search only |

Embedding is optional. Without it, BM25 keyword search still works.

## Supported Formats

DOCX, HWPX, XLSX, PPTX, PDF, TXT, MD — auto-extends when new parsers are added to FieldCure.DocumentParsers.

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
| `systemPrompt` | Custom system prompt (null = built-in default) |

## Data Storage

Knowledge base data at `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{kb-id}\`:
- `config.json` — knowledge base configuration (created by app)
- `rag.db` — SQLite database (chunks, embeddings, FTS5 index, file hashes, indexing lock)

## Requirements

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or later

## Links

- [GitHub](https://github.com/fieldcure/fieldcure-mcp-rag)
- [MCP Specification](https://modelcontextprotocol.io)
