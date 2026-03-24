# FieldCure MCP RAG Server

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that indexes documents into a vector store and performs semantic search using cosine similarity. Built with C# and the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Features

- **3 MCP tools** — index documents, semantic search, chunk retrieval
- **Incremental indexing** — SHA256 change detection, only re-indexes modified files
- **Orphan cleanup** — automatically removes DB entries for deleted files
- **Korean-optimized chunking** — sentence boundary splitting for Korean (습니다./해요.), decimal protection, parenthesis-aware
- **OpenAI-compatible embeddings** — works with Ollama, LM Studio, OpenAI, Azure OpenAI, Groq, Together AI
- **SIMD-accelerated search** — cosine similarity via `System.Numerics.Vector`
- **SQLite storage** — WAL mode, single-file database, zero configuration
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

- [.NET 9.0 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) or later
- An embedding provider (Ollama, OpenAI, etc.)

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

| Variable | Default | Description |
|----------|---------|-------------|
| `EMBEDDING_BASE_URL` | `http://localhost:11434` | Embedding API base URL |
| `EMBEDDING_API_KEY` | *(empty)* | API key (empty for local servers) |
| `EMBEDDING_MODEL` | `nomic-embed-text` | Model identifier |
| `EMBEDDING_DIMENSION` | `0` (auto-detect) | Vector dimension |

## Tools

| Tool | Description |
|------|-------------|
| `index_documents` | Index all supported documents in the context folder (incremental) |
| `search_documents` | Semantic search over indexed chunks |
| `get_document_chunk` | Retrieve full content of a specific chunk by ID |

### Supported Formats

Document formats are provided by [FieldCure.DocumentParsers](https://github.com/fieldcure/fieldcure-assiststudio):

- **DOCX** — Microsoft Word
- **HWPX** — Korean standard document (OWPML)
- **TXT, MD** — Plain text / Markdown

Additional formats are automatically supported when new parsers are registered.

## Architecture

```
src/FieldCure.Mcp.Rag/
├── Program.cs                  # Entry point, DI setup, stdio transport
├── RagContext.cs               # DI service container
├── Embedding/
│   ├── IEmbeddingProvider.cs   # Embedding abstraction
│   ├── OpenAiCompatibleEmbeddingProvider.cs
│   ├── NullEmbeddingProvider.cs
│   └── EmbeddingProviderFactory.cs
├── Storage/
│   └── SqliteVectorStore.cs    # SQLite + SIMD cosine similarity
├── Chunking/
│   └── TextChunker.cs          # Korean/English sentence-aware chunking
├── Tools/
│   ├── IndexDocumentsTool.cs   # Incremental indexing with orphan cleanup
│   ├── SearchDocumentsTool.cs  # Vector similarity search
│   └── GetDocumentChunkTool.cs # Chunk retrieval
└── Models/
    ├── DocumentChunk.cs
    └── SearchResult.cs
```

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
