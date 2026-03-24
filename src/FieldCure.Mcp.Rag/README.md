# FieldCure.Mcp.Rag

**MCP RAG server for document indexing and semantic vector search** — chunks documents, generates embeddings via OpenAI-compatible APIs, and performs cosine similarity search over SQLite storage.

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

## Tools (3)

| Tool | Description |
|------|-------------|
| `index_documents` | Index all supported documents (incremental, SHA256 change detection) |
| `search_documents` | Semantic search over indexed chunks with cosine similarity |
| `get_document_chunk` | Retrieve full content of a specific chunk by ID |

## Supported Formats

DOCX, HWPX, TXT, MD — auto-extends when new parsers are added to FieldCure.DocumentParsers.

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `EMBEDDING_BASE_URL` | `http://localhost:11434` | Embedding API base URL |
| `EMBEDDING_API_KEY` | *(empty)* | API key (empty for local servers) |
| `EMBEDDING_MODEL` | `nomic-embed-text` | Model identifier |
| `EMBEDDING_DIMENSION` | `0` (auto-detect) | Vector dimension |

## Requirements

- [.NET 9.0 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) or later

## Links

- [GitHub](https://github.com/fieldcure/fieldcure-mcp-rag)
- [MCP Specification](https://modelcontextprotocol.io)
