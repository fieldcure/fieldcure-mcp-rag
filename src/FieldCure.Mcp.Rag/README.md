# FieldCure.Mcp.Rag

> Requires [Ollama](https://ollama.ai) 0.4.0 or later when using Ollama for embedding or contextualization.

**MCP RAG server with hybrid BM25 + vector search and AI-powered chunk contextualization** — indexes documents from configured source paths, enriches chunks with AI-generated context and keywords, generates embeddings, and performs keyword (FTS5) and semantic (cosine similarity) search with Reciprocal Rank Fusion.

<!-- mcp-name: io.github.fieldcure/rag -->

## Install

```bash
dotnet tool install -g FieldCure.Mcp.Rag
```

## Commands

```
fieldcure-mcp-rag
├── serve         --base-path <path>                         # Multi-KB MCP search server (stdio)
├── exec          --path <kb-path> [--force] [--partial ...]  # Headless indexing
├── exec-queue    --queue-file <path> [--sweep-all]           # Sequential queue orchestrator
└── prune-orphans --base-path <path>                         # Delete orphan KB folders
```

- **serve** — read-only MCP server serving all KBs under the base path. Lazy-loads per KB.
- **exec** — headless indexing with 2-commit model, binary-split failure isolation, deferred retry.
- **exec-queue** — sequential orchestrator for queued indexing requests. No GPU contention.
- **prune-orphans** — deletes GUID-named folders without config.json. Protects backups.

API keys:
- **`serve` (stdio)** — environment variable (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, etc.) → MCP Elicitation fallback on the first tool call that needs a key. Session cache, max 2 re-elicits.
- **`exec` / `exec-queue`** (headless batch) — environment variable only. If unset, the run soft-fails with a clear message.

## Quick Start

```json
{
  "id": "my-kb-001",
  "name": "Project Docs",
  "sourcePaths": ["C:\\Users\\me\\Documents\\project-docs"],
  "embedding": {
    "provider": "openai",
    "model": "text-embedding-3-small",
    "apiKeyPreset": "OpenAI"
  }
}
```

```bash
# Index
fieldcure-mcp-rag exec --path "%LOCALAPPDATA%\FieldCure\Mcp.Rag\my-kb-001"

# Serve
fieldcure-mcp-rag serve --base-path "%LOCALAPPDATA%\FieldCure\Mcp.Rag"
```

### Claude Desktop

```json
{
  "mcpServers": {
    "rag": {
      "command": "fieldcure-mcp-rag",
      "args": ["serve", "--base-path", "C:\\Users\\me\\AppData\\Local\\FieldCure\\Mcp.Rag"],
      "env": {
        "OPENAI_API_KEY": "sk-..."
      }
    }
  }
}
```

## Tools (7)

| Tool | Description |
|------|-------------|
| `list_knowledge_bases` | List all KBs with status |
| `search_documents` | Hybrid BM25 + vector search (`auto`, `bm25`, `vector`) |
| `get_document_chunk` | Retrieve full chunk content by ID |
| `start_reindex` | Queue indexing request (scope merge, force/deferred, orchestrator spawn) |
| `cancel_reindex` | Remove pending queue entry |
| `get_index_info` | Index metadata + queue state (status/position/deferred/error) |
| `check_changes` | Dry-run filesystem scan. No API calls |

## config.json Reference

| Field | Description |
|-------|-------------|
| `id` | Knowledge base identifier |
| `name` | Display name |
| `sourcePaths` | Folders to index |
| `contextualizer.provider` | `"anthropic"`, `"openai"`, `"ollama"`, or empty |
| `contextualizer.model` | Model ID |
| `contextualizer.apiKeyPreset` | Env var mapping: `"OpenAI"` → `OPENAI_API_KEY` |
| `embedding.*` | Same structure as contextualizer |
| `embedding.keepAlive` | Ollama: VRAM retention (default `"5m"`) |
| `embedding.numCtx` | Ollama: context window (default 8192, contextualizer only) |
| `systemPrompt` | Custom contextualization prompt |

## Supported Formats

DOCX, HWPX, XLSX, PPTX, PDF, TXT, MD. Scanned PDFs without a text layer fall back to Tesseract OCR **on Windows only** — see "Platform support" below.

## Platform support

Cross-platform on Windows, Linux, macOS. Text extraction from all supported document formats works everywhere. The optional OCR package (`FieldCure.DocumentParsers.Ocr`, ships Tesseract native binaries) is referenced conditionally in the server's `.csproj` via `$([MSBuild]::IsOSPlatform('Windows'))`, so Linux and macOS builds are pure managed code and scanned-PDF pages on those platforms yield empty text.

## Requirements

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or later

## See Also

Part of the [AssistStudio ecosystem](https://github.com/fieldcure/fieldcure-assiststudio#packages).

- [GitHub](https://github.com/fieldcure/fieldcure-mcp-rag)
- [MCP Specification](https://modelcontextprotocol.io)
