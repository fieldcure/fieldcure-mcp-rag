using System.Net;
using System.Text.Json;
using FieldCure.Mcp.Rag.Configuration;
using FieldCure.Mcp.Rag.Embedding;

namespace FieldCure.Mcp.Rag.Tests.Embedding;

[TestClass]
public class GeminiEmbeddingProviderTests
{
    /// <summary>
    /// Captures the most recent outgoing request body and lets the test
    /// supply a canned response. Avoids a third-party HTTP mocking dependency.
    /// </summary>
    sealed class StubHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    static HttpResponseMessage JsonOk(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    static string EmbedContentResponse(float[] values)
    {
        var arr = string.Join(",", values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        return $"{{\"embedding\":{{\"values\":[{arr}]}}}}";
    }

    static GeminiEmbeddingProvider Build(StubHandler handler, int dimension = 0)
        => new(apiKey: "test-key", model: "gemini-embedding-2",
               dimension: dimension, baseUrl: "https://example.invalid/v1beta", handler: handler);

    [TestMethod]
    public async Task EmbedAsync_uses_RETRIEVAL_DOCUMENT_taskType()
    {
        var handler = new StubHandler(_ => JsonOk(EmbedContentResponse(new float[] { 1f, 0f, 0f, 0f })));
        var provider = Build(handler, dimension: 4);

        await provider.EmbedAsync("hello");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.AreEqual("RETRIEVAL_DOCUMENT", doc.RootElement.GetProperty("taskType").GetString());
    }

    [TestMethod]
    public async Task EmbedQueryAsync_uses_RETRIEVAL_QUERY_taskType()
    {
        var handler = new StubHandler(_ => JsonOk(EmbedContentResponse(new float[] { 1f, 0f, 0f, 0f })));
        var provider = Build(handler, dimension: 4);

        await provider.EmbedQueryAsync("hello");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.AreEqual("RETRIEVAL_QUERY", doc.RootElement.GetProperty("taskType").GetString());
    }

    [TestMethod]
    public async Task EmbedAsync_includes_outputDimensionality_when_dimension_set()
    {
        var handler = new StubHandler(_ => JsonOk(EmbedContentResponse(new float[] { 1f, 0f, 0f, 0f })));
        var provider = Build(handler, dimension: 1536);

        await provider.EmbedAsync("hello");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.IsTrue(doc.RootElement.TryGetProperty("outputDimensionality", out var dim));
        Assert.AreEqual(1536, dim.GetInt32());
    }

    [TestMethod]
    public async Task EmbedAsync_omits_outputDimensionality_when_dimension_zero()
    {
        var handler = new StubHandler(_ => JsonOk(EmbedContentResponse(new float[3072])));
        var provider = Build(handler, dimension: 0);

        await provider.EmbedAsync("hello");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("outputDimensionality", out _),
            "outputDimensionality must be omitted when dimension == 0 so the API default applies.");
    }

    [TestMethod]
    public async Task Sub3072_response_is_L2_normalized()
    {
        // API may return un-normalized values when output_dimensionality < 3072.
        var handler = new StubHandler(_ => JsonOk(EmbedContentResponse(new float[] { 3f, 4f, 0f, 0f })));
        var provider = Build(handler, dimension: 4);

        var vec = await provider.EmbedAsync("x");

        var norm = Math.Sqrt(vec.Select(v => (double)v * v).Sum());
        Assert.AreEqual(1.0, norm, 1e-5, "Sub-3072 vectors must be L2-normalized client-side.");
    }

    [TestMethod]
    public async Task Full3072_response_is_passed_through_without_normalization()
    {
        // Pre-normalized full-length output: any non-unit vector should survive untouched.
        var raw = new float[3072];
        raw[0] = 5f; // length 5, not 1 — verifies normalization is skipped.
        var handler = new StubHandler(_ => JsonOk(EmbedContentResponse(raw)));
        var provider = Build(handler, dimension: 0);

        var vec = await provider.EmbedAsync("x");

        Assert.AreEqual(5f, vec[0], "Full-length 3072 output must NOT be re-normalized.");
    }

    [TestMethod]
    public async Task Oversize_input_is_truncated_before_send()
    {
        var handler = new StubHandler(_ => JsonOk(EmbedContentResponse(new float[] { 1f, 0f, 0f, 0f })));
        var provider = Build(handler, dimension: 4);

        var oversize = new string('가', GeminiEmbeddingProvider.SafeInputChars + 1000);
        await provider.EmbedAsync(oversize);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var sent = doc.RootElement
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        Assert.AreEqual(GeminiEmbeddingProvider.SafeInputChars, sent!.Length);
    }

    [TestMethod]
    public async Task Non_success_status_throws_with_response_body_in_message()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"API key invalid\"}}",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var provider = Build(handler);

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => provider.EmbedAsync("hello"));

        Assert.IsTrue(ex.Message.Contains("API key invalid"),
            $"Exception message must surface API error body. Got: {ex.Message}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [TestMethod]
    public void Constructor_rejects_empty_api_key()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new GeminiEmbeddingProvider(apiKey: "", model: "gemini-embedding-2"));
    }

    [TestMethod]
    public void Factory_creates_GeminiEmbeddingProvider_for_gemini_kind()
    {
        var config = new ProviderConfig
        {
            Provider = "gemini",
            Model = "gemini-embedding-2",
            Dimension = 1536,
        };

        var provider = EmbeddingProviderFactory.Create(config, apiKey: "test-key");

        Assert.IsInstanceOfType(provider, typeof(GeminiEmbeddingProvider));
    }

    [TestMethod]
    public void Factory_creates_OllamaEmbeddingProvider_for_ollama_kind()
    {
        var config = new ProviderConfig { Provider = "ollama", Model = "nomic-embed-text" };
        var provider = EmbeddingProviderFactory.Create(config, apiKey: "");
        Assert.IsInstanceOfType(provider, typeof(OllamaEmbeddingProvider));
    }

    [TestMethod]
    public void Factory_creates_OpenAi_provider_for_openai_kind()
    {
        var config = new ProviderConfig { Provider = "openai", Model = "text-embedding-3-small" };
        var provider = EmbeddingProviderFactory.Create(config, apiKey: "test-key");
        Assert.IsInstanceOfType(provider, typeof(OpenAiCompatibleEmbeddingProvider));
    }

    [TestMethod]
    public void Factory_throws_for_unknown_provider_kind()
    {
        var config = new ProviderConfig { Provider = "bogus", Model = "x" };
        Assert.ThrowsExactly<NotSupportedException>(
            () => EmbeddingProviderFactory.Create(config, apiKey: ""));
    }

    [TestMethod]
    public void EmbeddingBatchSizes_resolves_gemini_embedding_2_to_100()
    {
        Assert.AreEqual(100, EmbeddingBatchSizes.Resolve("gemini", "gemini-embedding-2"));
    }
}
