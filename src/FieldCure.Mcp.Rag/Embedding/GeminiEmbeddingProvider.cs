using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FieldCure.Mcp.Rag.Embedding;

/// <summary>
/// Gemini retrieval task type. Drives the <c>taskType</c> parameter on the
/// <c>:embedContent</c> / <c>:batchEmbedContents</c> endpoints, which selects
/// an asymmetric embedding optimized for the document or query side.
/// See <see href="https://ai.google.dev/gemini-api/docs/embeddings#task-types"/>.
/// </summary>
public enum GeminiTaskType
{
    /// <summary>Indexing-time embedding for documents stored in the vector store.</summary>
    RetrievalDocument,

    /// <summary>Query-time embedding for user queries searched against documents.</summary>
    RetrievalQuery,

    /// <summary>Symmetric comparisons (deduplication, clustering).</summary>
    SemanticSimilarity,
}

/// <summary>
/// Embedding provider using Google's native Gemini embedding API.
/// Supports asymmetric retrieval via <c>task_type</c> and Matryoshka dimension
/// truncation via <c>output_dimensionality</c>.
/// </summary>
/// <remarks>
/// Uses the native endpoint (<c>/v1beta/models/{model}:embedContent</c>) rather
/// than the OpenAI compatibility layer, because the latter does not expose
/// <c>task_type</c> — forfeiting the retrieval quality boost this class exists
/// for. Multimodal inputs (image/audio/video) are out of scope and will be
/// handled by a separate provider in v2.0; this class is text only.
/// </remarks>
public sealed class GeminiEmbeddingProvider : IEmbeddingProvider
{
    internal const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    internal const int PreNormalizedDimension = 3072;

    // Character ceiling assuming ~2 chars/token for mixed KR/EN. Gemini's hard
    // limit is 8192 tokens; 12k chars leaves headroom for tokenizer variance
    // and contextualizer overhead. Truncation here turns a guaranteed 400 from
    // the API into a degraded-but-successful embedding.
    internal const int SafeInputChars = 12_000;

    readonly HttpClient _http;
    readonly string _apiKey;
    readonly string _model;
    readonly string _baseUrl;
    int _dimension;

    /// <inheritdoc/>
    public int Dimension => _dimension;

    /// <inheritdoc/>
    public string ModelId => _model;

    /// <param name="apiKey">Gemini API key (resolved from <c>GEMINI_API_KEY</c>).</param>
    /// <param name="model">Model id (e.g., <c>gemini-embedding-2</c>).</param>
    /// <param name="dimension">
    /// Requested output dimension. 0 = API default (3072). Recommended
    /// non-zero values: 768, 1536, 3072. Values below 3072 trigger client-side
    /// L2 normalization, as the API pre-normalizes only the full-length output.
    /// </param>
    public GeminiEmbeddingProvider(string apiKey, string model, int dimension = 0)
        : this(apiKey, model, dimension, baseUrl: null, handler: null)
    {
    }

    /// <summary>
    /// Test seam: allows injection of a custom <see cref="HttpMessageHandler"/>
    /// (for request interception) and a base URL override. Production code
    /// uses the public constructor.
    /// </summary>
    internal GeminiEmbeddingProvider(
        string apiKey,
        string model,
        int dimension,
        string? baseUrl,
        HttpMessageHandler? handler)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Gemini API key required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Gemini model required.", nameof(model));

        _apiKey = apiKey;
        _model = model;
        _dimension = dimension;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc/>
    /// <remarks>Uses <see cref="GeminiTaskType.RetrievalDocument"/> (indexing side).</remarks>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => EmbedSingleAsync(text, GeminiTaskType.RetrievalDocument, ct);

    /// <inheritdoc/>
    /// <remarks>Uses <see cref="GeminiTaskType.RetrievalQuery"/> (search side).</remarks>
    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default)
        => EmbedSingleAsync(query, GeminiTaskType.RetrievalQuery, ct);

    async Task<float[]> EmbedSingleAsync(string text, GeminiTaskType taskType, CancellationToken ct)
    {
        var request = BuildRequest(text, taskType);
        var url = $"{_baseUrl}/models/{_model}:embedContent?key={_apiKey}";

        var response = await _http.PostAsJsonAsync(url, request, ct);
        await ThrowIfNotSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<EmbedContentResponse>(ct)
                   ?? throw new InvalidOperationException("Empty Gemini embedding response.");

        var vec = body.Embedding?.Values
                  ?? throw new InvalidOperationException("Gemini response missing embedding values.");

        if (_dimension == 0)
            _dimension = vec.Length;

        // MRL truncation below 3072 is not pre-normalized by the API. Normalize
        // here to preserve cosine similarity correctness in the vector store.
        // Removing this normalization silently degrades recall — see XML doc
        // on EmbedQueryAsync for the broader asymmetric retrieval contract.
        if (vec.Length < PreNormalizedDimension)
            L2Normalize(vec);

        return vec;
    }

    /// <inheritdoc/>
    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        // Batch path is indexing-only by construction (search calls are single
        // and go through EmbedQueryAsync). Tag every item RETRIEVAL_DOCUMENT.
        var requests = texts
            .Select(t => BuildRequest(t, GeminiTaskType.RetrievalDocument))
            .ToArray();

        var url = $"{_baseUrl}/models/{_model}:batchEmbedContents?key={_apiKey}";
        var body = new { requests };

        var response = await _http.PostAsJsonAsync(url, body, ct);
        await ThrowIfNotSuccessAsync(response, ct);

        var result = await response.Content.ReadFromJsonAsync<BatchEmbedContentsResponse>(ct)
                     ?? throw new InvalidOperationException("Empty Gemini batch response.");

        var vectors = result.Embeddings
            .Select(e => e.Values ?? throw new InvalidOperationException("Missing values in batch item."))
            .ToArray();

        if (_dimension == 0 && vectors.Length > 0)
            _dimension = vectors[0].Length;

        if (vectors.Length > 0 && vectors[0].Length < PreNormalizedDimension)
            foreach (var v in vectors) L2Normalize(v);

        return vectors;
    }

    object BuildRequest(string text, GeminiTaskType taskType)
    {
        if (text.Length > SafeInputChars)
            text = text[..SafeInputChars];

        // outputDimensionality is omitted when 0 so the API default (3072) applies.
        if (_dimension > 0)
        {
            return new
            {
                model = $"models/{_model}",
                content = new { parts = new[] { new { text } } },
                taskType = TaskTypeString(taskType),
                outputDimensionality = _dimension,
            };
        }

        return new
        {
            model = $"models/{_model}",
            content = new { parts = new[] { new { text } } },
            taskType = TaskTypeString(taskType),
        };
    }

    static string TaskTypeString(GeminiTaskType t) => t switch
    {
        GeminiTaskType.RetrievalDocument => "RETRIEVAL_DOCUMENT",
        GeminiTaskType.RetrievalQuery => "RETRIEVAL_QUERY",
        GeminiTaskType.SemanticSimilarity => "SEMANTIC_SIMILARITY",
        _ => "RETRIEVAL_DOCUMENT",
    };

    /// <summary>
    /// L2-normalizes the vector in place. Required for Gemini MRL outputs
    /// below 3072 dimensions — only the full-length output is pre-normalized
    /// by the API. Removing this normalization silently degrades cosine
    /// similarity correctness in the vector store.
    /// </summary>
    static void L2Normalize(float[] vector)
    {
        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
            sumSq += (double)vector[i] * vector[i];

        var norm = Math.Sqrt(sumSq);
        if (norm < 1e-12) return; // degenerate null embedding; leave as-is

        var scale = (float)(1.0 / norm);
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= scale;
    }

    /// <summary>
    /// Throws <see cref="HttpRequestException"/> carrying the Gemini response
    /// body (truncated to 2 KB) on non-success status. Google returns
    /// structured error details in the body; default HttpClient behavior of
    /// discarding it makes diagnostics impossible.
    /// </summary>
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
            $"Gemini embed API returned {(int)response.StatusCode} {response.StatusCode}: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }

    sealed class EmbedContentResponse
    {
        [JsonPropertyName("embedding")]
        public ContentEmbedding? Embedding { get; set; }
    }

    sealed class BatchEmbedContentsResponse
    {
        [JsonPropertyName("embeddings")]
        public List<ContentEmbedding> Embeddings { get; set; } = [];
    }

    sealed class ContentEmbedding
    {
        [JsonPropertyName("values")]
        public float[]? Values { get; set; }
    }
}
