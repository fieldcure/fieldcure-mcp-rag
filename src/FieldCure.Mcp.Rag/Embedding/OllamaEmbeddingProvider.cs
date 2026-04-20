using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Embedding provider using Ollama's native <c>/api/embed</c> endpoint.
/// Supports <c>keep_alive</c> to prevent cold starts between bursty indexing runs.
/// Requires Ollama 0.4.0 or later.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    readonly HttpClient _http;
    readonly string _model;
    readonly string _baseUrl;
    readonly string _keepAlive;
    int _dimension;

    /// <inheritdoc/>
    public int Dimension => _dimension;

    /// <inheritdoc/>
    public string ModelId => _model;

    public OllamaEmbeddingProvider(
        string baseUrl, string model, string keepAlive, int dimension = 0)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _keepAlive = keepAlive;
        _dimension = dimension;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <inheritdoc/>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await EmbedBatchAsync([text], ct);
        return result[0];
    }

    /// <inheritdoc/>
    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var input = texts.Count == 1 ? texts[0] : (object)texts;

        var request = new
        {
            model = _model,
            input,
            keep_alive = _keepAlive
        };

        var response = await _http.PostAsJsonAsync($"{_baseUrl}/api/embed", request, ct);
        await ThrowIfNotSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct)
                   ?? throw new InvalidOperationException("Empty embedding response.");

        if (_dimension == 0 && body.Embeddings.Count > 0)
            _dimension = body.Embeddings[0].Length;

        return [.. body.Embeddings];
    }

    /// <summary>
    /// Throws an <see cref="HttpRequestException"/> carrying the Ollama response
    /// body (truncated to 2 KB) when the HTTP status code is not successful.
    /// </summary>
    /// <param name="response">Response to validate.</param>
    /// <param name="ct">Cancellation token for the body read.</param>
    static async Task ThrowIfNotSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) { body = $"<failed to read response body: {ex.GetType().Name}: {ex.Message}>"; }

        const int MaxBodyChars = 2048;
        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars] + $"… [truncated, full length {body.Length}]";

        throw new HttpRequestException(
            $"Ollama embed API returned {(int)response.StatusCode} {response.StatusCode}: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }

    sealed class OllamaEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; set; } = [];
    }
}
