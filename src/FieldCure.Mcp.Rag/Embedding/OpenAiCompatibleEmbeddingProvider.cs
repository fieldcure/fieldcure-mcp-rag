using System.Net.Http.Json;

namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Embedding provider for any OpenAI-compatible REST API.
/// Covers OpenAI, Azure OpenAI, Ollama, LM Studio, Groq, and others.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
{
    readonly HttpClient _http;
    readonly string _model;
    readonly string _baseUrl;
    int _dimension;

    /// <inheritdoc/>
    public int Dimension => _dimension;

    /// <inheritdoc/>
    public string ModelId => _model;

    /// <param name="baseUrl">API base URL (e.g., "http://localhost:11434" for Ollama).</param>
    /// <param name="apiKey">API key. Pass empty string for local servers.</param>
    /// <param name="model">Model identifier string.</param>
    /// <param name="dimension">
    /// Expected embedding dimension. If 0, determined by probing the API on first call.
    /// </param>
    public OpenAiCompatibleEmbeddingProvider(
        string baseUrl, string apiKey, string model, int dimension = 0)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _dimension = dimension;

        _http = new HttpClient();
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <inheritdoc/>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new { model = _model, input = text };
        var response = await _http.PostAsJsonAsync(
            $"{_baseUrl}/v1/embeddings", request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct)
                   ?? throw new InvalidOperationException("Empty embedding response.");

        var embedding = body.Data[0].Embedding;

        if (_dimension == 0)
            _dimension = embedding.Length;

        return embedding;
    }

    /// <inheritdoc/>
    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var request = new { model = _model, input = texts };
        var response = await _http.PostAsJsonAsync(
            $"{_baseUrl}/v1/embeddings", request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct)
                   ?? throw new InvalidOperationException("Empty embedding response.");

        var results = body.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToArray();

        if (_dimension == 0 && results.Length > 0)
            _dimension = results[0].Length;

        return results;
    }

    record EmbeddingResponse(List<EmbeddingData> Data);
    record EmbeddingData(int Index, float[] Embedding);
}
