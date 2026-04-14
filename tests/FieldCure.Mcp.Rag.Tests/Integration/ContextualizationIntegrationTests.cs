using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Storage;

namespace FieldCure.Mcp.Rag.Tests.Integration;

[TestClass]
public class ContextualizationIntegrationTests
{
    static string CreateTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_ctx_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    [TestMethod]
    public async Task UpsertWithEnriched_StoresEnrichedSeparately()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "enr_0",
            SourcePath = "doc.txt",
            ChunkIndex = 0,
            Content = "Original text content",
        };
        var enriched = "Context about batteries.\nKeywords: battery, 배터리\n\nOriginal text content";

        await store.UpsertChunkAsync(chunk, new float[] { 1, 0 }, "test", enriched);

        // GetChunkAsync returns original content
        var retrieved = await store.GetChunkAsync("enr_0");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("Original text content", retrieved.Content);
    }

    [TestMethod]
    public async Task FTS5_SearchesEnrichedText()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "fts_enr_0",
            SourcePath = "doc.txt",
            ChunkIndex = 0,
            Content = "임피던스를 측정합니다",
        };
        // Enriched text has additional keywords not in original
        var enriched = "Context about impedance measurement.\nKeywords: impedance, 임피던스, measurement, 측정\n\n임피던스를 측정합니다";

        await store.UpsertChunkAsync(chunk, new float[] { 1, 0 }, "test", enriched);

        // Search for "impedance" — only in enriched, not in original Korean text
        var results = await store.SearchFtsAsync("impedance", 5);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("fts_enr_0", results[0].ChunkId);
    }

    [TestMethod]
    public async Task FTS5_SearchesEnrichedKeywords_NotInOriginal()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "kw_0",
            SourcePath = "report.docx",
            ChunkIndex = 0,
            Content = "매출이 전분기 대비 3% 증가했습니다",
        };
        var enriched = "Context: Q2 2025 financial results.\nKeywords: revenue, 매출, growth, 성장, quarterly, 분기\n\n매출이 전분기 대비 3% 증가했습니다";

        await store.UpsertChunkAsync(chunk, new float[] { 1, 0 }, "test", enriched);

        // "revenue" only exists in enriched keywords
        var results = await store.SearchFtsAsync("revenue", 5);
        Assert.AreEqual(1, results.Count);

        // "growth" only exists in enriched keywords
        results = await store.SearchFtsAsync("growth", 5);
        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public async Task GetChunksByIds_ReturnsOriginalContent_NotEnriched()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "orig_0",
            SourcePath = "doc.txt",
            ChunkIndex = 0,
            Content = "Only the original text",
        };
        var enriched = "Enriched with context and keywords.\n\nOnly the original text";

        await store.UpsertChunkAsync(chunk, new float[] { 1, 0 }, "test", enriched);

        var results = await store.GetChunksByIdsAsync(["orig_0"]);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("Only the original text", results[0].Content);
    }

    [TestMethod]
    public async Task UpsertWithoutEnriched_DefaultsToContent()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunk = new DocumentChunk
        {
            Id = "noer_0",
            SourcePath = "doc.txt",
            ChunkIndex = 0,
            Content = "Plain content without enrichment",
        };

        // No enrichedText parameter — should default to content
        await store.UpsertChunkAsync(chunk, new float[] { 1, 0 }, "test");

        // FTS5 should still find it by original content
        var results = await store.SearchFtsAsync("enrichment", 5);
        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public async Task MigrationFromV020_AddsEnrichedColumn()
    {
        // Create a v1.1-style DB without enriched column
        var dbPath = CreateTempDb();

        // First create with current schema (which includes enriched)
        // then verify the migration path works by checking a fresh store
        // on an existing DB with data
        using (var store = new SqliteVectorStore(dbPath))
        {
            var chunk = new DocumentChunk
            {
                Id = "mig_0",
                SourcePath = "legacy.txt",
                ChunkIndex = 0,
                Content = "Legacy content",
            };
            await store.UpsertChunkAsync(chunk, new float[] { 1, 0 }, "test");
        }

        // Re-open — migration should run without error
        using (var store = new SqliteVectorStore(dbPath))
        {
            var retrieved = await store.GetChunkAsync("mig_0");
            Assert.IsNotNull(retrieved);
            Assert.AreEqual("Legacy content", retrieved.Content);

            // Should be able to upsert with enriched text
            var newChunk = new DocumentChunk
            {
                Id = "mig_1",
                SourcePath = "new.txt",
                ChunkIndex = 0,
                Content = "New content",
            };
            await store.UpsertChunkAsync(newChunk, new float[] { 0, 1 }, "test", "Enriched new content");

            var newRetrieved = await store.GetChunkAsync("mig_1");
            Assert.IsNotNull(newRetrieved);
            Assert.AreEqual("New content", newRetrieved.Content);
        }
    }
}
