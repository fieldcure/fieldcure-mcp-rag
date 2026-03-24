using FieldCure.Mcp.Rag;
using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// args[0] = context folder path (required)
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: fieldcure-mcp-rag <context-folder>");
    return 1;
}

var contextFolder = Path.GetFullPath(args[0]);
if (!Directory.Exists(contextFolder))
    Directory.CreateDirectory(contextFolder);

Console.Error.WriteLine($"Context folder: {contextFolder}");

// Embedding provider: read from environment variables
var embeddingProvider = EmbeddingProviderFactory.CreateFromEnvironment();
Console.Error.WriteLine($"Embedding model: {embeddingProvider.ModelId}");

// SQLite vector store
var dbDir = Path.Combine(contextFolder, ".rag");
Directory.CreateDirectory(dbDir);
var dbPath = Path.Combine(dbDir, "rag_index.db");
var store = new SqliteVectorStore(dbPath);

// Text chunker
var chunker = new TextChunker();

// RAG context for DI
var ragContext = new RagContext(contextFolder, store, embeddingProvider, chunker);

var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddSingleton(ragContext)
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "fieldcure-mcp-rag",
            Version = "0.1.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
await app.RunAsync();

store.Dispose();
return 0;
