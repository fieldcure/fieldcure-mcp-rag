# FieldCure.Mcp.Rag

**MCP RAG server with hybrid BM25 + vector search and AI-powered chunk contextualization** — chunks documents, enriches chunks with AI-generated context and keywords, generates embeddings via OpenAI-compatible APIs, and performs keyword (FTS5) and semantic (cosine similarity) search with Reciprocal Rank Fusion.

[![NuGet](https://img.shields.io/nuget/v/FieldCure.Mcp.Rag)](https://www.nuget.org/packages/FieldCure.Mcp.Rag)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/fieldcure/fieldcure-mcp-rag/blob/main/LICENSE)

## Install

```bash
dotnet tool install -g FieldCure.Mcp.Rag
```

## Quick Start

```bash
fieldcure-mcp-rag "C:\Users\me\Documents\RagContext"
```

Pass a context folder as argument. Documents in this folder will be indexed.

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

## Tools (4)

| Tool | Description |
|------|-------------|
| `index_documents` | Index all supported documents (incremental, SHA256 change detection). Accepts optional `system_prompt` for per-folder contextualization |
| `search_documents` | Hybrid BM25 + vector search with Reciprocal Rank Fusion |
| `get_document_chunk` | Retrieve full content of a specific chunk by ID |
| `get_index_info` | Returns index metadata (file/chunk counts, prompt config, stale-index detection). Internal — for host application use |

## Search Modes

| Mode | When | Description |
|------|------|-------------|
| `hybrid` | Embedding server + query >= 3 chars | BM25 keyword + vector semantic, fused via RRF |
| `bm25_only` | No embedding server configured | FTS5 trigram keyword search only |
| `vector_only` | Query tokens all < 3 chars | Cosine similarity search only |

Embedding server is optional. Without it, BM25 keyword search still works.

## Supported Formats

DOCX, HWPX, XLSX, PPTX, TXT, MD — auto-extends when new parsers are added to FieldCure.DocumentParsers.

## Environment Variables

### Embedding

| Variable | Default | Description |
|----------|---------|-------------|
| `EMBEDDING_BASE_URL` | `http://localhost:11434` | Embedding API base URL |
| `EMBEDDING_API_KEY` | *(empty)* | API key (empty for local servers) |
| `EMBEDDING_MODEL` | `nomic-embed-text` | Model identifier |
| `EMBEDDING_DIMENSION` | `0` (auto-detect) | Vector dimension |

### Chunk Contextualization (v0.3.0+)

| Variable | Default | Description |
|----------|---------|-------------|
| `CONTEXTUALIZER_PROVIDER` | `openai` | Provider: `openai` or `anthropic` |
| `CONTEXTUALIZER_BASE_URL` | *(empty)* | API base URL (e.g., `http://localhost:11434` for Ollama) |
| `CONTEXTUALIZER_API_KEY` | *(empty)* | API key (empty for local servers) |
| `CONTEXTUALIZER_MODEL` | *(empty)* | Model identifier. If empty, contextualization is disabled |
| `CONTEXTUALIZER_SYSTEM_PROMPT` | *(built-in)* | Custom system prompt for domain-specific keyword extraction. If empty, uses the default prompt |

When configured, each chunk is enriched with AI-generated context and normalized keywords during indexing. Search uses enriched text; responses return original text.

Custom system prompts allow domain-specific optimization — e.g., an EIS researcher may want different keyword extraction than a legal professional.

### Per-Folder System Prompt (v0.6.0+)

The `index_documents` tool accepts an optional `system_prompt` parameter for per-folder customization. Prompt resolution priority:

1. **Tool parameter** — explicit `system_prompt` in `index_documents` call (saved to DB)
2. **DB stored value** — previously saved custom prompt for this folder
3. **Environment variable** — `CONTEXTUALIZER_SYSTEM_PROMPT`
4. **Built-in default** — code-level default prompt

Only custom prompts are stored in DB. When `system_prompt` is null, the built-in default is used — so code updates automatically improve existing indices on re-index.

The `get_index_info` tool returns `is_prompt_stale: true` when the index was built with an older built-in prompt (hash mismatch), enabling the host app to notify users.

## Data Storage

Index data is stored at `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{folder_hash}\`:
- `rag_index.db` — SQLite database (chunks, embeddings, FTS5 index, file hashes, index metadata)

Existing v0.1.0 indices at `{contextFolder}/.rag/` are auto-migrated on first run.

## Requirements

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or later

## Links

- [GitHub](https://github.com/fieldcure/fieldcure-mcp-rag)
- [MCP Specification](https://modelcontextprotocol.io)
