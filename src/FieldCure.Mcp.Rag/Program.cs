using System.Security.Cryptography;
using System.Text;
using FieldCure.Mcp.Rag;
using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Search;
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

// Data root: %LOCALAPPDATA%/FieldCure/Mcp.Rag/{folderHash}/
var dataRoot = ComputeDataRoot(contextFolder);
Directory.CreateDirectory(dataRoot);

// Auto-migrate from legacy .rag/ path
var legacyDbPath = Path.Combine(contextFolder, ".rag", "rag_index.db");
var dbPath = Path.Combine(dataRoot, "rag_index.db");
if (File.Exists(legacyDbPath) && !File.Exists(dbPath))
{
    File.Copy(legacyDbPath, dbPath);
    Console.Error.WriteLine($"Migrated index from {legacyDbPath} to {dbPath}");
}

Console.Error.WriteLine($"Data root: {dataRoot}");

// Embedding provider: read from environment variables
var embeddingProvider = EmbeddingProviderFactory.CreateFromEnvironment();
Console.Error.WriteLine($"Embedding model: {embeddingProvider.ModelId}");

// SQLite vector store
var store = new SqliteVectorStore(dbPath);

// Text chunker
var chunker = new TextChunker();

// Hybrid searcher
var searcher = new HybridSearcher(store, embeddingProvider);

// RAG context for DI
var ragContext = new RagContext(contextFolder, dataRoot, store, embeddingProvider, chunker, searcher);

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
            Version = "0.2.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
await app.RunAsync();

store.Dispose();
return 0;

static string ComputeDataRoot(string contextFolder)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(contextFolder));
    var folderHash = Convert.ToHexString(hash)[..8].ToLowerInvariant();
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FieldCure", "Mcp.Rag", folderHash);
}
