using System.Net.Http.Json;
using System.Text.Json;
using FieldCure.Mcp.Rag.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// Enriches chunks using the Anthropic Messages API (/v1/messages).
/// Use for Claude models (Haiku, Sonnet, Opus).
/// </summary>
public sealed class AnthropicChunkContextualizer : IChunkContextualizer
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger _logger;
    private string _systemPrompt;

    /// <summary>
    /// Initializes the Anthropic contextualizer.
    /// </summary>
    /// <param name="apiKey">Anthropic API key (x-api-key header).</param>
    /// <param name="model">Model identifier (e.g., "claude-haiku-4-5-20251001").</param>
    /// <param name="baseUrl">API base URL. Default: "https://api.anthropic.com".</param>
    /// <param name="systemPrompt">Custom system prompt. Empty to use default.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public AnthropicChunkContextualizer(
        string apiKey, string model,
        string baseUrl = "https://api.anthropic.com",
        string systemPrompt = "",
        ILogger? logger = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _model = model;
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

    /// <inheritdoc />
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
                max_tokens = ChunkContextualizerHelper.DefaultMaxTokens,
                system = _systemPrompt,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var response = await _http.PostAsJsonAsync("/v1/messages", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var output = result
                .GetProperty("content")[0]
                .GetProperty("text")
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
