using System.Net.Http.Json;
using System.Text.Json;
using FieldCure.Mcp.Rag.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// Enriches chunks using Ollama's native <c>/api/chat</c> endpoint.
/// Supports <c>keep_alive</c> and <c>num_ctx</c> parameters that the
/// OpenAI-compatible shim ignores. Requires Ollama 0.4.0 or later.
/// </summary>
public sealed class OllamaChunkContextualizer : IChunkContextualizer
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _keepAlive;
    private readonly int _numCtx;
    private readonly ILogger _logger;
    private string _systemPrompt;

    public OllamaChunkContextualizer(
        string baseUrl, string model,
        string keepAlive, int numCtx,
        string systemPrompt = "",
        ILogger? logger = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromMinutes(10),
        };
        _model = model;
        _keepAlive = keepAlive;
        _numCtx = numCtx;
        _logger = logger ?? NullLogger.Instance;
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? ChunkContextualizerHelper.DefaultSystemPrompt
            : systemPrompt;
    }

    /// <summary>Gets or sets the system prompt used for enrichment.</summary>
    public string SystemPrompt
    {
        get => _systemPrompt;
        set => _systemPrompt = string.IsNullOrWhiteSpace(value)
            ? ChunkContextualizerHelper.DefaultSystemPrompt
            : value;
    }

    public async Task<EnrichResult> EnrichAsync(
        string chunkText,
        string? documentContext,
        string sourceFileName,
        int chunkIndex,
        int totalChunks,
        CancellationToken ct = default)
    {
        try
        {
            var prompt = ChunkContextualizerHelper.BuildPrompt(
                chunkText, documentContext, sourceFileName, chunkIndex, totalChunks);

            var request = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = _systemPrompt },
                    new { role = "user", content = prompt }
                },
                stream = false,
                keep_alive = _keepAlive,
                options = new { num_ctx = _numCtx }
            };

            var response = await _http.PostAsJsonAsync("/api/chat", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var output = result
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return EnrichResult.Success(
                ChunkContextualizerHelper.ParseEnrichedOutput(output, chunkText));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[RAG] Contextualization failed for chunk {ChunkIndex}/{TotalChunks} of {File}; " +
                "falling back to raw text. {ExceptionType}: {Message}",
                chunkIndex, totalChunks, sourceFileName, ex.GetType().Name, ex.Message);
            return EnrichResult.Failed(chunkText, ex);
        }
    }
}
