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
        await ThrowIfNotSuccessAsync(response, ct);

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
        await ThrowIfNotSuccessAsync(response, ct);

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

    /// <summary>
    /// Replacement for <c>EnsureSuccessStatusCode()</c> that reads the response body
    /// and includes it in the thrown exception. OpenAI-compatible APIs return the
    /// actual error reason (token limit, invalid model, etc.) only in the body, so
    /// the default HttpClient behaviour of discarding it makes diagnostics
    /// impossible. Throws <see cref="HttpRequestException"/> with the body in the
    /// message and the original <see cref="System.Net.HttpStatusCode"/> attached so
    /// the caller can classify the failure (e.g., 401/403 abort vs 5xx retry).
    /// </summary>
    static async Task ThrowIfNotSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception readEx)
        {
            body = $"<failed to read response body: {readEx.GetType().Name}: {readEx.Message}>";
        }

        // Truncate excessively long bodies (some providers echo back the full request
        // payload on 400). 2 KB is plenty for any structured error response.
        const int MaxBodyChars = 2048;
        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars] + $"… [truncated, full length {body.Length}]";

        throw new HttpRequestException(
            $"Embedding API returned {(int)response.StatusCode} {response.StatusCode}: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }

    record EmbeddingResponse(List<EmbeddingData> Data);
    record EmbeddingData(int Index, float[] Embedding);
}
