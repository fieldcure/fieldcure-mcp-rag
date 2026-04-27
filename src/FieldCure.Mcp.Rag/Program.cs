using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
#if WINDOWS_OCR
using FieldCure.DocumentParsers.Ocr;
#endif
#if WINDOWS_AUDIO
using FieldCure.DocumentParsers.Audio;
#endif
using FieldCure.Mcp.Rag;
using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Configuration;
using FieldCure.Mcp.Rag.Contextualization;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Indexing;
using FieldCure.Mcp.Rag.Services;
using FieldCure.Mcp.Rag.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// DP core auto-registers a text-only PdfParser. On Windows, upgrade the .pdf
// entry to an OCR-fallback parser; the OCR engine loads its native binaries
// lazily on the first scanned page. On non-Windows platforms the core
// text-only parser is used and scanned pages are silently skipped — DP.Ocr
// is not referenced on those platforms (see FieldCure.Mcp.Rag.csproj).
#if WINDOWS_OCR
if (OperatingSystem.IsWindows())
    DocumentParserFactoryOcrExtensions.AddOcrSupport(new LazyOcrEngine());
#endif

// Audio transcription is Windows-only (Whisper.net + NAudio Media Foundation).
// On non-Windows the Audio package is not referenced and matching files
// (.mp3 / .wav / .m4a / .ogg / .flac / .webm) silently return empty text.
#if WINDOWS_AUDIO
if (OperatingSystem.IsWindows())
{
    DocumentParserFactoryAudioExtensions.AddAudioSupport(new LazyAudioTranscriber());

    // Emit a one-shot diagnostic snapshot to stderr so users can self-diagnose
    // recommendation outcomes ("why am I getting Tiny instead of Medium?").
    // stdout is reserved for the MCP stdio transport.
    var probe = WhisperEnvironment.Probe();
    var recommended = WhisperEnvironment.RecommendModelSize();
    Console.Error.WriteLine(
        $"[Audio] CUDA={probe.CudaAvailable} Vulkan={probe.VulkanAvailable} " +
        $"RAM={probe.SystemRamBytes / (1024L * 1024 * 1024)}GB " +
        $"Cores={probe.LogicalCores} → recommended={recommended}");
}
#endif


if (args.Length > 0)
{
    Console.OutputEncoding = Encoding.UTF8;

    return args[0].ToLowerInvariant() switch
    {
        "exec" => await RunExecAsync(args),
        "exec-queue" => await RunExecQueueAsync(args),
        "serve" => await RunServeAsync(args),
        "prune-orphans" => await RunPruneOrphansAsync(args),
        _ => PrintUsage(),
    };
}

return PrintUsage();

// ── Serve Mode ──────────────────────────────────────────────────────────────

/// <summary>
/// Starts the multi-KB MCP search server in stdio mode.
/// </summary>
async Task<int> RunServeAsync(string[] args)
{
    var basePath = ParseArg(args, "--base-path");
    if (basePath is null) return PrintUsage();

    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Services
        .AddSingleton<ApiKeyResolverRegistry>()
        .AddSingleton(sp => new MultiKbContext(
            basePath,
            CreateEmbeddingProviderForBatch,
            sp.GetRequiredService<ILogger<MultiKbContext>>()))
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "fieldcure-mcp-rag",
                Title = "FieldCure RAG",
                Description = "Document search — hybrid BM25 + vector retrieval, multi-KB, incremental indexing",
                Version = GetPublicVersion(),
            };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();
    await app.RunAsync();

    return 0;
}

// ── Exec Mode ───────────────────────────────────────────────────────────────

/// <summary>
/// Runs headless indexing for a single knowledge base.
/// </summary>
async Task<int> RunExecAsync(string[] args)
{
    var kbPath = ParseArg(args, "--path");
    if (kbPath is null) return PrintUsage();

    var force = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
    var partial = ParseStringArg(args, "--partial");
    var verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)
        || a.Equals("-v", StringComparison.OrdinalIgnoreCase));

    var config = RagConfig.Load(kbPath);
    var dbPath = Path.Combine(kbPath, "rag.db");

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        builder.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
    });

    var logger = loggerFactory.CreateLogger<IndexingEngine>();
    var store = new SqliteVectorStore(dbPath);
    var chunker = new TextChunker(maxChars: config.Embedding.MaxChunkChars);
    var embeddingProvider = CreateEmbeddingProviderForBatch(config.Embedding);
    var contextualizer = CreateContextualizerForBatch(config.Contextualizer, loggerFactory);

    logger.LogInformation("Knowledge base: {Name} ({Id})", config.Name, config.Id);
    logger.LogInformation("Path: {Path}", kbPath);
    logger.LogInformation("Source paths: {Paths}", string.Join(", ", config.SourcePaths));
    logger.LogInformation("Embedding: {Provider}/{Model}", config.Embedding.Provider, config.Embedding.Model);
    logger.LogInformation("Contextualizer: {Type}", contextualizer.GetType().Name);

    var engine = new IndexingEngine(kbPath, config, store, embeddingProvider, chunker, contextualizer, logger);

    try
    {
        var result = await engine.RunAsync(force, partial, CancellationToken.None);
        return result.ExitCode;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Indexing failed.");
        return 1;
    }
    finally
    {
        // Cancel file must not linger into the next run regardless of how
        // this run ended (normal, cancel, or exception).
        var cancelPath = Path.Combine(kbPath, "cancel");
        if (File.Exists(cancelPath))
            try { File.Delete(cancelPath); } catch { /* best-effort */ }

        store.Dispose();
    }
}

// ── Exec-Queue Mode ─────────────────────────────────────────────────────────

/// <summary>
/// Runs the deferred queue orchestrator — processes KB entries sequentially.
/// </summary>
async Task<int> RunExecQueueAsync(string[] args)
{
    var queueFile = ParseArg(args, "--queue-file");
    if (queueFile is null) return PrintUsage();

    var verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)
        || a.Equals("-v", StringComparison.OrdinalIgnoreCase));

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        builder.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
    });

    var sweepAll = args.Any(a => a.Equals("--sweep-all", StringComparison.OrdinalIgnoreCase));

    return await ExecQueueRunner.RunAsync(queueFile, sweepAll, verbose, loggerFactory);
}

// ── Prune Orphans Mode ─────────────────────────────────────────────────────

/// <summary>
/// Scans the base path for orphan KB folders (no config.json) and deletes them.
/// </summary>
async Task<int> RunPruneOrphansAsync(string[] args)
{
    var basePath = ParseArg(args, "--base-path");
    if (basePath is null) return PrintUsage();

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
    });

    return await OrphanCleanupRunner.RunAsync(basePath, loggerFactory);
}

// ── Helpers ─────────────────────────────────────────────────────────────────

/// <summary>
/// Parses a named argument value from the command-line args (e.g., --base-path /foo).
/// </summary>
static string? ParseStringArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

/// <summary>
/// Parses a named path argument from the command line and returns it as an
/// absolute path (e.g., <c>--path ./foo</c> → <c>/abs/foo</c>).
/// </summary>
/// <param name="args">Command-line arguments.</param>
/// <param name="name">Argument name to match (case-insensitive).</param>
/// <returns>The absolute path, or <see langword="null"/> when the argument is absent.</returns>
static string? ParseArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(args[i + 1]);
    }

    return null;
}

/// <summary>
/// Creates an embedding provider from the given configuration using credential lookup.
/// </summary>
static IEmbeddingProvider CreateEmbeddingProviderForBatch(ProviderConfig config)
{
    if (string.IsNullOrEmpty(config.Model))
        return new NullEmbeddingProvider();

    var apiKey = ApiKeyEnvironment.ResolveOrEmpty(config.ApiKeyPreset);
    return EmbeddingProviderFactory.Create(config, apiKey);
}

/// <summary>
/// Creates the chunk contextualizer used by batch runs (exec / exec-queue).
/// Returns a <see cref="NullChunkContextualizer"/> when the provider model is
/// unset so the pipeline can proceed without contextualization.
/// </summary>
/// <param name="config">Contextualizer provider configuration.</param>
/// <param name="loggerFactory">Logger factory for provider diagnostics.</param>
/// <returns>The resolved contextualizer for batch execution.</returns>
static IChunkContextualizer CreateContextualizerForBatch(
    ProviderConfig config, ILoggerFactory loggerFactory)
{
    if (string.IsNullOrEmpty(config.Model))
        return new NullChunkContextualizer();

    var apiKey = ApiKeyEnvironment.ResolveOrEmpty(config.ApiKeyPreset);

    var baseUrl = config.BaseUrl ?? config.Provider.ToLowerInvariant() switch
    {
        "anthropic" => "https://api.anthropic.com",
        "ollama" => "http://localhost:11434",
        "openai" => "https://api.openai.com",
        _ => "http://localhost:11434",
    };

    if (config.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        return new AnthropicChunkContextualizer(
            apiKey, config.Model, baseUrl, logger: loggerFactory.CreateLogger<AnthropicChunkContextualizer>());

    if (config.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        return new OllamaChunkContextualizer(
            baseUrl, config.Model,
            config.KeepAlive ?? OllamaDefaults.KeepAlive,
            config.NumCtx ?? OllamaDefaults.NumCtx,
            logger: loggerFactory.CreateLogger<OllamaChunkContextualizer>());

    return new OpenAiChunkContextualizer(
        baseUrl, config.Model, apiKey, logger: loggerFactory.CreateLogger<OpenAiChunkContextualizer>());
}

/// <summary>
/// Prints CLI usage information to stderr and returns exit code 1.
/// </summary>
static int PrintUsage()
{
    Console.Error.WriteLine("FieldCure RAG — Document indexing and hybrid search engine");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  fieldcure-mcp-rag serve         --base-path <path>                         Start multi-KB MCP search server (stdio)");
    Console.Error.WriteLine("  fieldcure-mcp-rag exec          --path <kb-path> [--force] [-v]            Run headless indexing for a single KB");
    Console.Error.WriteLine("  fieldcure-mcp-rag exec-queue    --queue-file <path> [--sweep-all] [-v]     Process deferred queue sequentially");
    Console.Error.WriteLine("  fieldcure-mcp-rag prune-orphans --base-path <path>                         Delete orphan KB folders (no config.json)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Exit codes (exec mode):");
    Console.Error.WriteLine("  0  Succeeded");
    Console.Error.WriteLine("  1  Failed");
    Console.Error.WriteLine("  2  Cancelled");
    return 1;
}

/// <summary>
/// Returns the user-facing server version. Strips the SemVer 2.0 build-metadata
/// suffix (<c>+&lt;commit-sha&gt;</c>) that the .NET SDK auto-appends to
/// <see cref="AssemblyInformationalVersionAttribute"/>; that hash is only useful
/// to developers and just adds noise in client UIs. The assembly attribute
/// itself still carries the full string for diagnostic logs and debuggers.
/// </summary>
static string GetPublicVersion()
{
    var info = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;
    if (string.IsNullOrEmpty(info)) return "0.0.0";
    var plus = info.IndexOf('+');
    return plus > 0 ? info[..plus] : info;
}
