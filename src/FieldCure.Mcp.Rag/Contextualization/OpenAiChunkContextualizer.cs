using System.Net.Http.Json;
using System.Text.Json;
using FieldCure.Mcp.Rag.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// Enriches chunks using an OpenAI-compatible chat completion endpoint.
/// Works with OpenAI, Ollama, Groq, and any compatible API.
/// </summary>
public sealed class OpenAiChunkContextualizer : IChunkContextualizer
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger _logger;
    private string _systemPrompt;

    /// <summary>
    /// Initializes the contextualizer.
    /// </summary>
    /// <param name="baseUrl">API base URL (e.g., "http://localhost:11434" for Ollama).</param>
    /// <param name="model">Model identifier (e.g., "gemma3:4b").</param>
    /// <param name="apiKey">API key. Empty string for local servers.</param>
    /// <param name="systemPrompt">Custom system prompt. Empty to use default.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public OpenAiChunkContextualizer(
        string baseUrl, string model,
        string apiKey = "",
        string systemPrompt = "",
        ILogger? logger = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')) };
        _model = model;
        _logger = logger ?? NullLogger.Instance;
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? ChunkContextualizerHelper.DefaultSystemPrompt
            : systemPrompt;

        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
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
                temperature = 0.0,
                max_tokens = ChunkContextualizerHelper.DefaultMaxTokens
            };

            var response = await _http.PostAsJsonAsync("/v1/chat/completions", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var output = result
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return EnrichResult.Success(
                ChunkContextualizerHelper.ParseEnrichedOutput(output, chunkText));
        }
        catch (OperationCanceledException)
        {
            throw; // Do not swallow user cancellation
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
