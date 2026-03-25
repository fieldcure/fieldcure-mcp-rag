using System.Net.Http.Json;
using System.Text.Json;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// Enriches chunks using an OpenAI-compatible chat completion endpoint.
/// Works with OpenAI, Ollama, Groq, and any compatible API.
/// </summary>
public sealed class OpenAiChunkContextualizer : IChunkContextualizer
{
    private readonly HttpClient _http;
    private readonly string _model;

    /// <summary>
    /// Initializes the contextualizer.
    /// </summary>
    /// <param name="baseUrl">API base URL (e.g., "http://localhost:11434" for Ollama).</param>
    /// <param name="model">Model identifier (e.g., "gemma3:4b").</param>
    /// <param name="apiKey">API key. Empty string for local servers.</param>
    public OpenAiChunkContextualizer(string baseUrl, string model, string apiKey = "")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')) };
        _model = model;

        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
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
                messages = new[]
                {
                    new { role = "system", content = ChunkContextualizerHelper.SystemPrompt },
                    new { role = "user", content = prompt }
                },
                temperature = 0.0,
                max_tokens = 300
            };

            var response = await _http.PostAsJsonAsync("/v1/chat/completions", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var output = result
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
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
