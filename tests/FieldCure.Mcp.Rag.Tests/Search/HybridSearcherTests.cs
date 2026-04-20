using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Search;
using FieldCure.Mcp.Rag.Storage;

namespace FieldCure.Mcp.Rag.Tests.Search;

[TestClass]
public class HybridSearcherTests
{
    static string CreateTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    static async Task<SqliteVectorStore> CreateStoreWithData(string dbPath)
    {
        var store = new SqliteVectorStore(dbPath);

        // Insert chunks with embeddings and content for FTS5
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "h_0", SourcePath = "battery.txt", ChunkIndex = 0, Content = "전기화학 임피던스 분광법은 배터리 진단에 사용됩니다" },
            new float[] { 0.9f, 0.1f, 0.0f, 0.0f }, "test");
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "h_1", SourcePath = "battery.txt", ChunkIndex = 1, Content = "Battery impedance spectroscopy for EIS analysis" },
            new float[] { 0.8f, 0.2f, 0.0f, 0.0f }, "test");
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "h_2", SourcePath = "weather.txt", ChunkIndex = 0, Content = "Weather forecast for tomorrow will be sunny" },
            new float[] { 0.0f, 0.0f, 0.9f, 0.1f }, "test");

        return store;
    }

    [TestMethod]
    public async Task Bm25Only_WithNullEmbeddingProvider()
    {
        using var store = await CreateStoreWithData(CreateTempDb());
        var searcher = new HybridSearcher(store, new NullEmbeddingProvider());

        var result = await searcher.SearchAsync("임피던스", topK: 5, threshold: 0.3f);

        Assert.AreEqual(SearchMode.Bm25Only, result.Mode);
        Assert.IsTrue(result.Results.Count > 0);
        Assert.AreEqual("h_0", result.Results[0].ChunkId);
        Assert.IsFalse(string.IsNullOrEmpty(result.Results[0].Content));
    }

    [TestMethod]
    public async Task Hybrid_WithBothSearchesAvailable()
    {
        using var store = await CreateStoreWithData(CreateTempDb());
        // Use a simple fake embedding provider that returns a known vector
        var embedder = new FakeEmbeddingProvider(new float[] { 0.9f, 0.1f, 0.0f, 0.0f });
        var searcher = new HybridSearcher(store, embedder);

        var result = await searcher.SearchAsync("임피던스", topK: 5, threshold: 0.1f);

        Assert.AreEqual(SearchMode.Hybrid, result.Mode);
        Assert.IsTrue(result.Results.Count > 0);
        // h_0 should be boosted (appears in both BM25 and vector results)
        Assert.AreEqual("h_0", result.Results[0].ChunkId);
    }

    [TestMethod]
    public async Task VectorOnly_WhenFtsReturnsEmpty()
    {
        using var store = await CreateStoreWithData(CreateTempDb());
        var embedder = new FakeEmbeddingProvider(new float[] { 0.9f, 0.1f, 0.0f, 0.0f });
        var searcher = new HybridSearcher(store, embedder);

        // "ab" is < 3 chars, all tokens dropped → FTS5 returns empty → VectorOnly
        var result = await searcher.SearchAsync("ab", topK: 5, threshold: 0.1f);

        Assert.AreEqual(SearchMode.VectorOnly, result.Mode);
        Assert.IsTrue(result.Results.Count > 0);
    }

    [TestMethod]
    public async Task Results_HaveContent()
    {
        using var store = await CreateStoreWithData(CreateTempDb());
        var searcher = new HybridSearcher(store, new NullEmbeddingProvider());

        var result = await searcher.SearchAsync("impedance spectroscopy", topK: 5, threshold: 0.3f);

        foreach (var r in result.Results)
        {
            Assert.IsFalse(string.IsNullOrEmpty(r.Content));
            Assert.IsFalse(string.IsNullOrEmpty(r.SourcePath));
        }
    }

    [TestMethod]
    public async Task TotalChunksSearched_ReturnsCorrectCount()
    {
        using var store = await CreateStoreWithData(CreateTempDb());
        var searcher = new HybridSearcher(store, new NullEmbeddingProvider());

        var result = await searcher.SearchAsync("임피던스", topK: 5, threshold: 0.3f);

        Assert.AreEqual(3, result.TotalChunksSearched);
    }

    /// <summary>Test helper: returns a fixed embedding for any query.</summary>
    sealed class FakeEmbeddingProvider(float[] fixedEmbedding) : IEmbeddingProvider
    {
        public int Dimension => fixedEmbedding.Length;
        public string ModelId => "fake";

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(fixedEmbedding);

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult(texts.Select(_ => fixedEmbedding).ToArray());
    }
}
