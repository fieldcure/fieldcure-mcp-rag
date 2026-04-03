using System.Reflection;
using System.Text;
using FieldCure.DocumentParsers.Pdf;
using FieldCure.Mcp.Rag;
using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Configuration;
using FieldCure.Mcp.Rag.Contextualization;
using FieldCure.Mcp.Rag.Credentials;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Indexing;
using FieldCure.Mcp.Rag.Search;
using FieldCure.Mcp.Rag.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1416 // Platform compatibility — this tool targets Windows (AssistStudio integration)

// Register PDF parser
DocumentParserFactoryExtensions.AddPdfSupport();

if (args.Length > 0)
{
    Console.OutputEncoding = Encoding.UTF8;

    return args[0].ToLowerInvariant() switch
    {
        "exec" => await RunExecAsync(args),
        "serve" => await RunServeAsync(args),
        _ => PrintUsage(),
    };
}

return PrintUsage();

// ── Serve Mode ──────────────────────────────────────────────────────────────

async Task<int> RunServeAsync(string[] args)
{
    var kbPath = ParsePathArg(args);
    if (kbPath is null) return PrintUsage();

    var config = RagConfig.Load(kbPath);
    var dbPath = Path.Combine(kbPath, "rag.db");
    var store = new SqliteVectorStore(dbPath);
    var credentials = new CredentialService();

    // Embedding provider for search queries
    var embeddingProvider = CreateEmbeddingProvider(config.Embedding, credentials);
    var searcher = new HybridSearcher(store, embeddingProvider);

    // Serve mode uses NullChunkContextualizer (no indexing)
    var ragContext = new RagContext(kbPath, kbPath, store, embeddingProvider, new TextChunker(), searcher, new NullChunkContextualizer());

    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

    builder.Logging.ClearProviders();
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
                Version = typeof(Program).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "0.0.0",
            };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();
    await app.RunAsync();

    store.Dispose();
    return 0;
}

// ── Exec Mode ───────────────────────────────────────────────────────────────

async Task<int> RunExecAsync(string[] args)
{
    var kbPath = ParsePathArg(args);
    if (kbPath is null) return PrintUsage();

    var force = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));

    var config = RagConfig.Load(kbPath);
    var dbPath = Path.Combine(kbPath, "rag.db");

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
    });

    var logger = loggerFactory.CreateLogger<IndexingEngine>();
    var credentials = new CredentialService();
    var store = new SqliteVectorStore(dbPath);
    var chunker = new TextChunker();
    var embeddingProvider = CreateEmbeddingProvider(config.Embedding, credentials);
    var contextualizer = CreateContextualizer(config.Contextualizer, credentials);

    logger.LogInformation("Knowledge base: {Name} ({Id})", config.Name, config.Id);
    logger.LogInformation("Path: {Path}", kbPath);
    logger.LogInformation("Source paths: {Paths}", string.Join(", ", config.SourcePaths));
    logger.LogInformation("Embedding: {Provider}/{Model}", config.Embedding.Provider, config.Embedding.Model);
    logger.LogInformation("Contextualizer: {Type}", contextualizer.GetType().Name);

    var engine = new IndexingEngine(kbPath, config, store, embeddingProvider, chunker, contextualizer, logger);

    try
    {
        return await engine.RunAsync(force, CancellationToken.None);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Indexing failed.");
        return 1;
    }
    finally
    {
        store.Dispose();
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────────

static string? ParsePathArg(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--path", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(args[i + 1]);
    }

    return null;
}

static IEmbeddingProvider CreateEmbeddingProvider(ProviderConfig config, ICredentialService credentials)
{
    if (string.IsNullOrEmpty(config.Model))
        return new NullEmbeddingProvider();

    var apiKey = config.ApiKeyPreset is not null
        ? credentials.GetApiKey(config.ApiKeyPreset) ?? ""
        : "";

    var baseUrl = config.BaseUrl ?? config.Provider.ToLowerInvariant() switch
    {
        "ollama" => "http://localhost:11434",
        "openai" => "https://api.openai.com",
        _ => "http://localhost:11434",
    };

    return new OpenAiCompatibleEmbeddingProvider(baseUrl, apiKey, config.Model, config.Dimension);
}

static IChunkContextualizer CreateContextualizer(ProviderConfig config, ICredentialService credentials)
{
    if (string.IsNullOrEmpty(config.Model))
        return new NullChunkContextualizer();

    var apiKey = config.ApiKeyPreset is not null
        ? credentials.GetApiKey(config.ApiKeyPreset) ?? ""
        : "";

    var baseUrl = config.BaseUrl ?? config.Provider.ToLowerInvariant() switch
    {
        "anthropic" => "https://api.anthropic.com",
        "ollama" => "http://localhost:11434",
        "openai" => "https://api.openai.com",
        _ => "http://localhost:11434",
    };

    if (config.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        return new AnthropicChunkContextualizer(apiKey, config.Model, baseUrl);

    return new OpenAiChunkContextualizer(baseUrl, config.Model, apiKey);
}

static int PrintUsage()
{
    Console.Error.WriteLine("FieldCure RAG — Document indexing and hybrid search engine");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  fieldcure-mcp-rag serve --path <kb-path>           Start MCP search server (stdio)");
    Console.Error.WriteLine("  fieldcure-mcp-rag exec  --path <kb-path> [--force] Run headless indexing");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Exit codes (exec mode):");
    Console.Error.WriteLine("  0  Succeeded");
    Console.Error.WriteLine("  1  Failed");
    Console.Error.WriteLine("  2  Cancelled");
    return 1;
}
