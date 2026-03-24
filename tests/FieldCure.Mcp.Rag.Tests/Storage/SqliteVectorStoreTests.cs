using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Storage;

namespace FieldCure.Mcp.Rag.Tests.Storage;

[TestClass]
public class SqliteVectorStoreTests
{
    static string CreateTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    [TestMethod]
    public async Task UpsertAndGetChunk_RoundTrip()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "test_0",
            SourcePath = "doc.txt",
            ChunkIndex = 0,
            Content = "Hello world",
            CharOffset = 0,
        };
        var embedding = new float[] { 1.0f, 0.0f, 0.0f };

        await store.UpsertChunkAsync(chunk, embedding, "test-model");

        var retrieved = await store.GetChunkAsync("test_0");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("test_0", retrieved.Id);
        Assert.AreEqual("doc.txt", retrieved.SourcePath);
        Assert.AreEqual("Hello world", retrieved.Content);
    }

    [TestMethod]
    public async Task GetChunk_NotFound_ReturnsNull()
    {
        using var store = new SqliteVectorStore(CreateTempDb());
        var result = await store.GetChunkAsync("nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteBySourcePath_RemovesChunks()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "del_0",
            SourcePath = "to_delete.txt",
            ChunkIndex = 0,
            Content = "Delete me",
        };
        await store.UpsertChunkAsync(chunk, new float[] { 1, 0, 0 }, "test");

        await store.DeleteBySourcePathAsync("to_delete.txt");

        var retrieved = await store.GetChunkAsync("del_0");
        Assert.IsNull(retrieved);
    }

    [TestMethod]
    public async Task FileHash_SetAndGet()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var hash = await store.GetFileHashAsync("file.txt");
        Assert.IsNull(hash);

        await store.SetFileHashAsync("file.txt", "abc123");
        hash = await store.GetFileHashAsync("file.txt");
        Assert.AreEqual("abc123", hash);

        // Update hash
        await store.SetFileHashAsync("file.txt", "def456");
        hash = await store.GetFileHashAsync("file.txt");
        Assert.AreEqual("def456", hash);
    }

    [TestMethod]
    public async Task GetIndexedPaths_ReturnsAllPaths()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await store.SetFileHashAsync("a.txt", "hash1");
        await store.SetFileHashAsync("b.txt", "hash2");

        var paths = await store.GetIndexedPathsAsync();
        Assert.AreEqual(2, paths.Count);
        CollectionAssert.Contains(paths, "a.txt");
        CollectionAssert.Contains(paths, "b.txt");
    }

    [TestMethod]
    public async Task PurgeSourcePath_RemovesChunksAndFileIndex()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "purge_0",
            SourcePath = "purge.txt",
            ChunkIndex = 0,
            Content = "Purge me",
        };
        await store.UpsertChunkAsync(chunk, new float[] { 1, 0, 0 }, "test");
        await store.SetFileHashAsync("purge.txt", "hash");

        await store.PurgeSourcePathAsync("purge.txt");

        Assert.IsNull(await store.GetChunkAsync("purge_0"));
        Assert.IsNull(await store.GetFileHashAsync("purge.txt"));
    }

    [TestMethod]
    public async Task Search_FindsSimilarVectors()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        // Insert two chunks with different embeddings
        var chunk1 = new DocumentChunk
        {
            Id = "s_0", SourcePath = "doc.txt", ChunkIndex = 0, Content = "Battery impedance"
        };
        var chunk2 = new DocumentChunk
        {
            Id = "s_1", SourcePath = "doc.txt", ChunkIndex = 1, Content = "Weather forecast"
        };

        // chunk1 embedding is similar to query, chunk2 is orthogonal
        await store.UpsertChunkAsync(chunk1, new float[] { 0.9f, 0.1f, 0.0f, 0.0f }, "test");
        await store.UpsertChunkAsync(chunk2, new float[] { 0.0f, 0.0f, 0.9f, 0.1f }, "test");

        var query = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var results = await store.SearchAsync(query, topK: 2, threshold: 0.1f);

        Assert.IsTrue(results.Count >= 1);
        Assert.AreEqual("s_0", results[0].ChunkId);
        Assert.IsTrue(results[0].Score > results[^1].Score || results.Count == 1);
    }

    [TestMethod]
    public async Task Search_RespectsThreshold()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "t_0", SourcePath = "doc.txt", ChunkIndex = 0, Content = "Test"
        };
        await store.UpsertChunkAsync(chunk, new float[] { 0.0f, 1.0f, 0.0f }, "test");

        // Query is orthogonal to stored embedding
        var query = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await store.SearchAsync(query, topK: 5, threshold: 0.5f);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task GetTotalChunkCount_ReturnsCorrectCount()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        Assert.AreEqual(0, await store.GetTotalChunkCountAsync());

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "c_0", SourcePath = "a.txt", ChunkIndex = 0, Content = "A" },
            new float[] { 1, 0 }, "test");
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "c_1", SourcePath = "a.txt", ChunkIndex = 1, Content = "B" },
            new float[] { 0, 1 }, "test");

        Assert.AreEqual(2, await store.GetTotalChunkCountAsync());
    }

    // --- FTS5 Tests ---

    [TestMethod]
    public async Task SearchFts_EnglishText_FindsMatch()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "fts_0", SourcePath = "doc.txt", ChunkIndex = 0, Content = "Battery impedance spectroscopy analysis" },
            new float[] { 1, 0 }, "test");
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "fts_1", SourcePath = "doc.txt", ChunkIndex = 1, Content = "Weather forecast for tomorrow" },
            new float[] { 0, 1 }, "test");

        var results = await store.SearchFtsAsync("impedance", 5);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("fts_0", results[0].ChunkId);
        Assert.IsTrue(results[0].Score > 0);
    }

    [TestMethod]
    public async Task SearchFts_KoreanTrigram_FindsMatch()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "kr_0", SourcePath = "doc.hwpx", ChunkIndex = 0, Content = "전기화학 임피던스 분광법은 배터리 진단에 사용됩니다" },
            new float[] { 1, 0 }, "test");

        var results = await store.SearchFtsAsync("임피던스", 5);

        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual("kr_0", results[0].ChunkId);
    }

    [TestMethod]
    public async Task SearchFts_ShortTokensDropped()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "st_0", SourcePath = "doc.txt", ChunkIndex = 0, Content = "RS 임피던스 분석 결과를 확인합니다" },
            new float[] { 1, 0 }, "test");

        // "RS" (2 chars) is dropped, only "임피던스" (4 chars, >= 3) is searched
        var results = await store.SearchFtsAsync("RS 임피던스", 5);

        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual("st_0", results[0].ChunkId);
    }

    [TestMethod]
    public async Task SearchFts_AllTokensTooShort_ReturnsEmpty()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "as_0", SourcePath = "doc.txt", ChunkIndex = 0, Content = "Some content here" },
            new float[] { 1, 0 }, "test");

        var results = await store.SearchFtsAsync("RS ab", 5);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task DeleteBySourcePath_AlsoRemovesFtsRecords()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "df_0", SourcePath = "delete_me.txt", ChunkIndex = 0, Content = "Impedance analysis content" },
            new float[] { 1, 0 }, "test");

        // Verify FTS5 finds it before deletion
        var before = await store.SearchFtsAsync("Impedance", 5);
        Assert.IsTrue(before.Count > 0);

        await store.DeleteBySourcePathAsync("delete_me.txt");

        var after = await store.SearchFtsAsync("Impedance", 5);
        Assert.AreEqual(0, after.Count);
    }

    [TestMethod]
    public async Task GetChunksByIds_ReturnsMatchingChunks()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "bi_0", SourcePath = "a.txt", ChunkIndex = 0, Content = "First" },
            new float[] { 1, 0 }, "test");
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "bi_1", SourcePath = "a.txt", ChunkIndex = 1, Content = "Second" },
            new float[] { 0, 1 }, "test");
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "bi_2", SourcePath = "b.txt", ChunkIndex = 0, Content = "Third" },
            new float[] { 1, 1 }, "test");

        var results = await store.GetChunksByIdsAsync(["bi_0", "bi_2", "nonexistent"]);

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(c => c.Id == "bi_0"));
        Assert.IsTrue(results.Any(c => c.Id == "bi_2"));
    }

    [TestMethod]
    public async Task GetChunksByIds_IncludesTotalChunks()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        // Insert 3 chunks for same source
        for (int i = 0; i < 3; i++)
        {
            await store.UpsertChunkAsync(
                new DocumentChunk { Id = $"tc_{i}", SourcePath = "multi.txt", ChunkIndex = i, Content = $"Chunk {i}" },
                new float[] { 1, 0 }, "test");
        }

        var results = await store.GetChunksByIdsAsync(["tc_1"]);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(3, results[0].TotalChunks);
    }

    [TestMethod]
    public async Task GetChunksByIds_EmptyList_ReturnsEmpty()
    {
        using var store = new SqliteVectorStore(CreateTempDb());
        var results = await store.GetChunksByIdsAsync([]);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void BuildFtsQuery_FiltersShortTokens()
    {
        Assert.AreEqual("\"임피던스\"", SqliteVectorStore.BuildFtsQuery("RS 임피던스"));
        Assert.AreEqual("\"임피던스\" OR \"분광법\"", SqliteVectorStore.BuildFtsQuery("임피던스 분광법"));
        Assert.AreEqual("", SqliteVectorStore.BuildFtsQuery("RS ab"));
        Assert.AreEqual("", SqliteVectorStore.BuildFtsQuery(""));
        Assert.AreEqual("\"abc\"", SqliteVectorStore.BuildFtsQuery("abc"));
    }
}
