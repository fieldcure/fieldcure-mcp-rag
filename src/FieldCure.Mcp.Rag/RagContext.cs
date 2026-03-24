using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Storage;

namespace FieldCure.Mcp.Rag;

/// <summary>
/// Aggregates all RAG services needed by MCP tools.
/// Registered as a singleton in the DI container.
/// </summary>
public sealed class RagContext
{
    public string ContextFolder { get; }
    public SqliteVectorStore Store { get; }
    public IEmbeddingProvider EmbeddingProvider { get; }
    public TextChunker Chunker { get; }

    public RagContext(
        string contextFolder,
        SqliteVectorStore store,
        IEmbeddingProvider embeddingProvider,
        TextChunker chunker)
    {
        ContextFolder = contextFolder;
        Store = store;
        EmbeddingProvider = embeddingProvider;
        Chunker = chunker;
    }
}
