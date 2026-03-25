using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Contextualization;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Search;
using FieldCure.Mcp.Rag.Storage;

namespace FieldCure.Mcp.Rag;

/// <summary>
/// Aggregates all RAG services needed by MCP tools.
/// Registered as a singleton in the DI container.
/// </summary>
public sealed class RagContext
{
    public string ContextFolder { get; }
    public string DataRoot { get; }
    public SqliteVectorStore Store { get; }
    public IEmbeddingProvider EmbeddingProvider { get; }
    public TextChunker Chunker { get; }
    public HybridSearcher Searcher { get; }
    public IChunkContextualizer Contextualizer { get; }

    public RagContext(
        string contextFolder,
        string dataRoot,
        SqliteVectorStore store,
        IEmbeddingProvider embeddingProvider,
        TextChunker chunker,
        HybridSearcher searcher,
        IChunkContextualizer contextualizer)
    {
        ContextFolder = contextFolder;
        DataRoot = dataRoot;
        Store = store;
        EmbeddingProvider = embeddingProvider;
        Chunker = chunker;
        Searcher = searcher;
        Contextualizer = contextualizer;
    }
}
