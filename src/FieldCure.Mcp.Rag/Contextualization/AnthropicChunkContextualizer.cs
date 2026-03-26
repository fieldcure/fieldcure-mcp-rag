using System.Net.Http.Json;
using System.Text.Json;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// Enriches chunks using the Anthropic Messages API (/v1/messages).
/// Use for Claude models (Haiku, Sonnet, Opus).
/// </summary>
public sealed class AnthropicChunkContextualizer : IChunkContextualizer
{
    private readonly HttpClient _http;
    private readonly string _model;
    private string _systemPrompt;

    /// <summary>
    /// Initializes the Anthropic contextualizer.
    /// </summary>
    /// <param name="apiKey">Anthropic API key (x-api-key header).</param>
    /// <param name="model">Model identifier (e.g., "claude-haiku-4-5-20251001").</param>
    /// <param name="baseUrl">API base URL. Default: "https://api.anthropic.com".</param>
    /// <param name="systemPrompt">Custom system prompt. Empty to use default.</param>
    public AnthropicChunkContextualizer(string apiKey, string model, string baseUrl = "https://api.anthropic.com", string systemPrompt = "")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _model = model;
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

    public async Task<string> EnrichAsync(
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

            return ChunkContextualizerHelper.ParseEnrichedOutput(output, chunkText);
        }
        catch
        {
            // AI 호출 실패 → 원본 반환 (인덱싱 중단 안 함)
            return chunkText;
        }
    }
}
