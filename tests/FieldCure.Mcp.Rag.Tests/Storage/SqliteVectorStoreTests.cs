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
}
