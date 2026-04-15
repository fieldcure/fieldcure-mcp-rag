using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Storage;
using Microsoft.Data.Sqlite;

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

        await SeedFileWithChunkAsync(store, "file.txt", "fh_0", "abc123");
        hash = await store.GetFileHashAsync("file.txt");
        Assert.AreEqual("abc123", hash);

        // Update hash
        await SeedFileWithChunkAsync(store, "file.txt", "fh_0", "def456");
        hash = await store.GetFileHashAsync("file.txt");
        Assert.AreEqual("def456", hash);
    }

    [TestMethod]
    public async Task GetIndexedPaths_ReturnsAllPaths()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await SeedFileWithChunkAsync(store, "a.txt", "ip_0", "hash1");
        await SeedFileWithChunkAsync(store, "b.txt", "ip_1", "hash2");

        var paths = await store.GetIndexedPathsAsync();
        Assert.AreEqual(2, paths.Count);
        CollectionAssert.Contains(paths, "a.txt");
        CollectionAssert.Contains(paths, "b.txt");
    }

    [TestMethod]
    public async Task PurgeSourcePath_RemovesChunksAndFileIndex()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await SeedFileWithChunkAsync(store, "purge.txt", "purge_0", "hash");

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

    // --- ReplaceFileChunksAsync Tests ---

    [TestMethod]
    public async Task ReplaceFileChunks_EmbeddingFailure_PreservesOldData()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        // Seed existing data
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "old_0", SourcePath = "file.txt", ChunkIndex = 0, Content = "Old content" },
            new float[] { 1, 0 }, "test");
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "old_1", SourcePath = "file.txt", ChunkIndex = 1, Content = "Old content 2" },
            new float[] { 0, 1 }, "test");

        // Attempt replace with a bad embedding (wrong dimension causes no issue,
        // but we can simulate failure via null chunk to trigger NRE inside transaction)
        var newChunks = new[]
        {
            new DocumentChunk { Id = "new_0", SourcePath = "file.txt", ChunkIndex = 0, Content = "New content" },
        };
        // Provide mismatched embedding count to trigger ArgumentException before transaction
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            store.ReplaceFileChunksAsync("file.txt", newChunks, new float[0][], "test"));

        // Old data must still be intact
        var old0 = await store.GetChunkAsync("old_0");
        var old1 = await store.GetChunkAsync("old_1");
        Assert.IsNotNull(old0);
        Assert.IsNotNull(old1);
        Assert.AreEqual("Old content", old0.Content);
        Assert.AreEqual("Old content 2", old1.Content);
        Assert.AreEqual(2, await store.GetTotalChunkCountAsync());

        // FTS must still work for old data
        var fts = await store.SearchFtsAsync("Old content", 5);
        Assert.IsTrue(fts.Count > 0);
    }

    [TestMethod]
    public async Task ReplaceFileChunks_TransactionRollback_PreservesOldData()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        // Seed existing data
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "keep_0", SourcePath = "file.txt", ChunkIndex = 0, Content = "Keep me" },
            new float[] { 1, 0, 0 }, "test");

        // Attempt replace with a null Content to trigger NOT NULL constraint violation inside transaction
        var newChunks = new[]
        {
            new DocumentChunk { Id = "new_0", SourcePath = "file.txt", ChunkIndex = 0, Content = null! },
        };
        var embeddings = new[] { new float[] { 1, 0, 0 } };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.ReplaceFileChunksAsync("file.txt", newChunks, embeddings, "test"));

        // Old data must survive the rollback
        var kept = await store.GetChunkAsync("keep_0");
        Assert.IsNotNull(kept);
        Assert.AreEqual("Keep me", kept.Content);
    }

    [TestMethod]
    public async Task ReplaceFileChunks_NormalReplace_UpdatesAll()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        // Seed old data with 2 chunks
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "r_0", SourcePath = "doc.md", ChunkIndex = 0, Content = "Old A" },
            new float[] { 1, 0 }, "v1");
        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "r_1", SourcePath = "doc.md", ChunkIndex = 1, Content = "Old B" },
            new float[] { 0, 1 }, "v1");

        // Replace with 3 new chunks
        var newChunks = new[]
        {
            new DocumentChunk { Id = "r_0", SourcePath = "doc.md", ChunkIndex = 0, Content = "New A" },
            new DocumentChunk { Id = "r_1", SourcePath = "doc.md", ChunkIndex = 1, Content = "New B" },
            new DocumentChunk { Id = "r_2", SourcePath = "doc.md", ChunkIndex = 2, Content = "New C" },
        };
        var newEmbeddings = new[]
        {
            new float[] { 0.5f, 0.5f },
            new float[] { 0.3f, 0.7f },
            new float[] { 0.9f, 0.1f },
        };
        var chunkInfos = new ChunkWriteInfo[]
        {
            new() { EnrichedText = "Enriched A", Status = ChunkIndexStatus.Indexed, IsContextualized = true },
            new() { EnrichedText = "Enriched B", Status = ChunkIndexStatus.Indexed, IsContextualized = true },
            new() { EnrichedText = "Enriched C", Status = ChunkIndexStatus.Indexed, IsContextualized = true },
        };

        await store.ReplaceFileChunksAsync("doc.md", newChunks, newEmbeddings, "v2", chunkInfos);

        // Verify chunks updated
        Assert.AreEqual(3, await store.GetTotalChunkCountAsync());
        var c0 = await store.GetChunkAsync("r_0");
        Assert.IsNotNull(c0);
        Assert.AreEqual("New A", c0.Content);

        // Verify FTS updated (old content gone, new content searchable)
        var oldFts = await store.SearchFtsAsync("Old", 5);
        Assert.AreEqual(0, oldFts.Count);

        var newFts = await store.SearchFtsAsync("Enriched", 5);
        Assert.AreEqual(3, newFts.Count);

        // Verify embedding updated via vector search
        var results = await store.SearchAsync(new float[] { 0.9f, 0.1f }, topK: 1, threshold: 0.5f);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("r_2", results[0].ChunkId);
    }

    [TestMethod]
    public async Task ReplaceFileChunks_ConsecutiveReplace_CleansOldChunks()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        // First replace: 3 chunks
        var chunks1 = new[]
        {
            new DocumentChunk { Id = "h_0", SourcePath = "data.txt", ChunkIndex = 0, Content = "impedance spectroscopy alpha" },
            new DocumentChunk { Id = "h_1", SourcePath = "data.txt", ChunkIndex = 1, Content = "impedance spectroscopy beta" },
            new DocumentChunk { Id = "h_2", SourcePath = "data.txt", ChunkIndex = 2, Content = "impedance spectroscopy gamma" },
        };
        var emb1 = new[] { new float[] { 1, 0 }, new float[] { 0, 1 }, new float[] { 1, 1 } };
        await store.ReplaceFileChunksAsync("data.txt", chunks1, emb1, "test");

        Assert.AreEqual(3, await store.GetTotalChunkCountAsync());

        // Second replace: only 1 chunk (simulating file shrinkage)
        var chunks2 = new[]
        {
            new DocumentChunk { Id = "h_0", SourcePath = "data.txt", ChunkIndex = 0, Content = "electrochemical analysis result" },
        };
        var emb2 = new[] { new float[] { 0.5f, 0.5f } };
        await store.ReplaceFileChunksAsync("data.txt", chunks2, emb2, "test");

        // Only 1 chunk should remain
        Assert.AreEqual(1, await store.GetTotalChunkCountAsync());
        Assert.IsNull(await store.GetChunkAsync("h_1"));
        Assert.IsNull(await store.GetChunkAsync("h_2"));

        var remaining = await store.GetChunkAsync("h_0");
        Assert.IsNotNull(remaining);
        Assert.AreEqual("electrochemical analysis result", remaining.Content);

        // FTS should only find new content
        var v1Fts = await store.SearchFtsAsync("spectroscopy", 5);
        Assert.AreEqual(0, v1Fts.Count);

        var v2Fts = await store.SearchFtsAsync("electrochemical", 5);
        Assert.AreEqual(1, v2Fts.Count);
    }

    [TestMethod]
    public async Task ReplaceFileChunks_EmptyChunks_PreservesExisting()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "e_0", SourcePath = "file.txt", ChunkIndex = 0, Content = "Existing" },
            new float[] { 1, 0 }, "test");

        // Empty chunks list → early return, no deletion
        await store.ReplaceFileChunksAsync("file.txt", [], [], "test");

        var existing = await store.GetChunkAsync("e_0");
        Assert.IsNotNull(existing);
        Assert.AreEqual("Existing", existing.Content);
    }

    // --- Schema Migration Tests ---

    /// <summary>
    /// Simulates a v1.3 database (no v1.4 columns), opens it with current code,
    /// and verifies all new columns exist after migration.
    /// </summary>
    [TestMethod]
    public void Migration_V03ToV04_AddsAllStatusColumns()
    {
        var dbPath = CreateTempDb();

        // Create a v1.3-era database: only base schema + enriched column, no v1.4 columns.
        // We do this by creating tables manually without the v1.4 migration.
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(connStr))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE chunks (
                    id           TEXT PRIMARY KEY,
                    source_path  TEXT NOT NULL,
                    chunk_index  INTEGER NOT NULL,
                    content      TEXT NOT NULL,
                    enriched     TEXT,
                    char_offset  INTEGER NOT NULL DEFAULT 0,
                    metadata     TEXT NOT NULL DEFAULT '{}'
                );

                CREATE TABLE embeddings (
                    chunk_id     TEXT PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
                    model        TEXT NOT NULL,
                    embedding    BLOB NOT NULL
                );

                CREATE TABLE file_index (
                    source_path  TEXT PRIMARY KEY,
                    file_hash    TEXT NOT NULL,
                    indexed_at   TEXT NOT NULL
                );

                CREATE TABLE index_metadata (
                    key   TEXT PRIMARY KEY,
                    value TEXT
                );

                CREATE TABLE _indexing_lock (
                    id       INTEGER PRIMARY KEY CHECK (id = 1),
                    pid      INTEGER NOT NULL,
                    started  TEXT NOT NULL,
                    current  INTEGER NOT NULL DEFAULT 0,
                    total    INTEGER NOT NULL DEFAULT 0
                );

                CREATE VIRTUAL TABLE chunks_fts USING fts5(chunk_id, content, tokenize = 'trigram');

                -- Insert seed data
                INSERT INTO chunks (id, source_path, chunk_index, content, enriched, char_offset)
                VALUES ('seed_0', 'doc.txt', 0, 'Hello world', 'Enriched hello', 0);

                INSERT INTO file_index (source_path, file_hash, indexed_at)
                VALUES ('doc.txt', 'abc123', '2026-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        // Open with SqliteVectorStore — triggers InitializeSchema → MigrateV04StatusColumns
        using var store = new SqliteVectorStore(dbPath);

        // Verify all v1.4 columns exist by querying them
        using (var conn = new SqliteConnection(connStr))
        {
            conn.Open();

            // chunks columns
            AssertColumnExists(conn, "chunks", "status");
            AssertColumnExists(conn, "chunks", "is_contextualized");
            AssertColumnExists(conn, "chunks", "last_error");
            AssertColumnExists(conn, "chunks", "retry_count");

            // file_index columns
            AssertColumnExists(conn, "file_index", "status");
            AssertColumnExists(conn, "file_index", "chunks_raw");
            AssertColumnExists(conn, "file_index", "chunks_pending");
            AssertColumnExists(conn, "file_index", "last_error");
            AssertColumnExists(conn, "file_index", "last_error_stage");

            // _indexing_lock columns
            AssertColumnExists(conn, "_indexing_lock", "current_stage");
            AssertColumnExists(conn, "_indexing_lock", "failed_count");
            AssertColumnExists(conn, "_indexing_lock", "provider_health");
        }
    }

    /// <summary>
    /// Verifies that running migration twice is idempotent (no errors on second run).
    /// </summary>
    [TestMethod]
    public void Migration_V04_Idempotent()
    {
        var dbPath = CreateTempDb();

        // First open — creates schema + runs migration
        using (var store = new SqliteVectorStore(dbPath)) { }

        // Second open — migration runs again, should not throw
        using (var store = new SqliteVectorStore(dbPath)) { }

        // Verify columns still exist
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        AssertColumnExists(conn, "chunks", "status");
        AssertColumnExists(conn, "file_index", "last_error_stage");
        AssertColumnExists(conn, "_indexing_lock", "provider_health");
    }

    /// <summary>
    /// Verifies that existing data survives the v1.4 migration intact.
    /// </summary>
    [TestMethod]
    public async Task Migration_V04_PreservesExistingData()
    {
        var dbPath = CreateTempDb();

        // Seed data with v1.3 store (first open creates schema + migrates)
        using (var store = new SqliteVectorStore(dbPath))
        {
            var chunks = new[] { new DocumentChunk { Id = "m_0", SourcePath = "test.md", ChunkIndex = 0, Content = "Original content" } };
            var embs = new[] { new float[] { 1, 0 } };
            var infos = new[] { new ChunkWriteInfo { EnrichedText = "Enriched content", Status = ChunkIndexStatus.Indexed, IsContextualized = true } };
            var fileInfo = new FileWriteInfo { FileHash = "hash123", Status = FileIndexStatus.Ready, ChunksRaw = 0, ChunksPending = 0 };
            await store.ReplaceFileChunksAsync("test.md", chunks, embs, "test-model", infos, fileInfo);
        }

        // Re-open (migration runs again)
        using var store2 = new SqliteVectorStore(dbPath);

        // Verify original data preserved
        var chunk = await store2.GetChunkAsync("m_0");
        Assert.IsNotNull(chunk);
        Assert.AreEqual("Original content", chunk.Content);
        Assert.AreEqual("test.md", chunk.SourcePath);

        var hash = await store2.GetFileHashAsync("test.md");
        Assert.AreEqual("hash123", hash);

        // Verify new columns have defaults
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, is_contextualized, last_error, retry_count FROM chunks WHERE id = 'm_0'";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(0, reader.GetInt32(0));  // status = Indexed (0)
        Assert.AreEqual(1, reader.GetInt32(1));  // is_contextualized = true (1)
        Assert.IsTrue(reader.IsDBNull(2));       // last_error = NULL
        Assert.AreEqual(0, reader.GetInt32(3));  // retry_count = 0

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT status, chunks_raw, chunks_pending, last_error, last_error_stage FROM file_index WHERE source_path = 'test.md'";
        using var reader2 = cmd2.ExecuteReader();
        Assert.IsTrue(reader2.Read());
        Assert.AreEqual(0, reader2.GetInt32(0));  // status = Ready (0)
        Assert.AreEqual(0, reader2.GetInt32(1));  // chunks_raw = 0
        Assert.AreEqual(0, reader2.GetInt32(2));  // chunks_pending = 0
        Assert.IsTrue(reader2.IsDBNull(3));       // last_error = NULL
        Assert.IsTrue(reader2.IsDBNull(4));       // last_error_stage = NULL
    }

    static void AssertColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        Assert.Fail($"Column '{column}' not found in table '{table}'.");
    }

    // --- Phase 3a: Status API Tests ---

    [TestMethod]
    public async Task ReplaceFileChunks_WithChunkInfo_PersistsStatus()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunks = new[]
        {
            new DocumentChunk { Id = "si_0", SourcePath = "f.txt", ChunkIndex = 0, Content = "Raw chunk" },
        };
        var embeddings = new[] { new float[] { 1, 0 } };
        var chunkInfos = new ChunkWriteInfo[]
        {
            new() { EnrichedText = "Raw chunk", Status = ChunkIndexStatus.IndexedRaw, IsContextualized = false, LastError = "LLM timeout" },
        };

        await store.ReplaceFileChunksAsync("f.txt", chunks, embeddings, "test", chunkInfos);

        // Verify via raw SQL
        var dbPath = typeof(SqliteVectorStore).GetField("_connectionString",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(store)!.ToString()!;
        using var conn = new SqliteConnection(dbPath);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, is_contextualized, last_error FROM chunks WHERE id = 'si_0'";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual((int)ChunkIndexStatus.IndexedRaw, reader.GetInt32(0));
        Assert.AreEqual(0, reader.GetInt32(1)); // is_contextualized = false
        Assert.AreEqual("LLM timeout", reader.GetString(2));
    }

    [TestMethod]
    public async Task ReplaceFileChunks_WithFileInfo_UpsertsFileIndex()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var chunks = new[]
        {
            new DocumentChunk { Id = "fi_0", SourcePath = "doc.md", ChunkIndex = 0, Content = "Content" },
        };
        var embeddings = new[] { new float[] { 1, 0 } };
        var chunkInfos = new ChunkWriteInfo[]
        {
            new() { EnrichedText = "Content", Status = ChunkIndexStatus.IndexedRaw, IsContextualized = false },
        };
        var fileInfo = new FileWriteInfo
        {
            FileHash = "abc123",
            Status = FileIndexStatus.Degraded,
            ChunksRaw = 1,
            ChunksPending = 0,
            LastError = "3/5 chunks contextualization failed",
            LastErrorStage = "contextualize",
        };

        await store.ReplaceFileChunksAsync("doc.md", chunks, embeddings, "test", chunkInfos, fileInfo);

        // Verify file_index
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_hash, status, chunks_raw, chunks_pending, last_error, last_error_stage FROM file_index WHERE source_path = 'doc.md'";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("abc123", reader.GetString(0));
        Assert.AreEqual((int)FileIndexStatus.Degraded, reader.GetInt32(1));
        Assert.AreEqual(1, reader.GetInt32(2));
        Assert.AreEqual(0, reader.GetInt32(3));
        Assert.AreEqual("3/5 chunks contextualization failed", reader.GetString(4));
        Assert.AreEqual("contextualize", reader.GetString(5));
    }

    [TestMethod]
    public async Task ReplaceFileChunks_WithFileInfo_UpdatesExistingRow()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var chunk1 = new[] { new DocumentChunk { Id = "uf_0", SourcePath = "doc.md", ChunkIndex = 0, Content = "V1" } };
        var emb1 = new[] { new float[] { 1, 0 } };
        var info1 = new[] { new ChunkWriteInfo { EnrichedText = "V1", Status = ChunkIndexStatus.Indexed, IsContextualized = true } };
        var file1 = new FileWriteInfo { FileHash = "hash1", Status = FileIndexStatus.Ready, ChunksRaw = 0, ChunksPending = 0 };
        await store.ReplaceFileChunksAsync("doc.md", chunk1, emb1, "test", info1, file1);

        // Second replace with different hash and status
        var chunk2 = new[] { new DocumentChunk { Id = "uf_0", SourcePath = "doc.md", ChunkIndex = 0, Content = "V2" } };
        var emb2 = new[] { new float[] { 0, 1 } };
        var info2 = new[] { new ChunkWriteInfo { EnrichedText = "V2", Status = ChunkIndexStatus.IndexedRaw, IsContextualized = false } };
        var file2 = new FileWriteInfo { FileHash = "hash2", Status = FileIndexStatus.Degraded, ChunksRaw = 1, ChunksPending = 0 };
        await store.ReplaceFileChunksAsync("doc.md", chunk2, emb2, "test", info2, file2);

        // Verify only one row, with latest values
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM file_index WHERE source_path = 'doc.md'";
        Assert.AreEqual(1L, cmd.ExecuteScalar());

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT file_hash, status FROM file_index WHERE source_path = 'doc.md'";
        using var reader = cmd2.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("hash2", reader.GetString(0));
        Assert.AreEqual((int)FileIndexStatus.Degraded, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task ReplaceFileChunks_TransactionRollback_FileIndexAlsoRollsBack()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        // Seed existing data with file_index
        var seedChunk = new[] { new DocumentChunk { Id = "rb_0", SourcePath = "file.txt", ChunkIndex = 0, Content = "Old" } };
        var seedEmb = new[] { new float[] { 1, 0 } };
        var seedInfo = new[] { new ChunkWriteInfo { EnrichedText = "Old", Status = ChunkIndexStatus.Indexed, IsContextualized = true } };
        var seedFile = new FileWriteInfo { FileHash = "old_hash", Status = FileIndexStatus.Ready, ChunksRaw = 0, ChunksPending = 0 };
        await store.ReplaceFileChunksAsync("file.txt", seedChunk, seedEmb, "test", seedInfo, seedFile);

        // Attempt replace that will fail (null Content → InvalidOperationException)
        var badChunks = new[] { new DocumentChunk { Id = "rb_0", SourcePath = "file.txt", ChunkIndex = 0, Content = null! } };
        var badEmb = new[] { new float[] { 1, 0 } };
        var badFile = new FileWriteInfo { FileHash = "new_hash", Status = FileIndexStatus.Degraded, ChunksRaw = 1, ChunksPending = 0 };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.ReplaceFileChunksAsync("file.txt", badChunks, badEmb, "test", fileInfo: badFile));

        // Both chunks and file_index must be preserved from before
        var chunk = await store.GetChunkAsync("rb_0");
        Assert.IsNotNull(chunk);
        Assert.AreEqual("Old", chunk.Content);

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_hash, status FROM file_index WHERE source_path = 'file.txt'";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("old_hash", reader.GetString(0));
        Assert.AreEqual((int)FileIndexStatus.Ready, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task ReplaceFileChunks_NullInfo_BackwardCompatible()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunks = new[]
        {
            new DocumentChunk { Id = "bc_0", SourcePath = "compat.txt", ChunkIndex = 0, Content = "Compat" },
        };
        var embeddings = new[] { new float[] { 1, 0 } };

        // No chunkInfo, no fileInfo — backward compatible
        await store.ReplaceFileChunksAsync("compat.txt", chunks, embeddings, "test");

        var chunk = await store.GetChunkAsync("bc_0");
        Assert.IsNotNull(chunk);
        Assert.AreEqual("Compat", chunk.Content);
    }

    [TestMethod]
    public async Task MarkFileAsFailed_PreservesExistingChunks()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        // Seed 2 chunks + file_index
        var chunks = new[]
        {
            new DocumentChunk { Id = "mf_0", SourcePath = "data.pdf", ChunkIndex = 0, Content = "Chunk A" },
            new DocumentChunk { Id = "mf_1", SourcePath = "data.pdf", ChunkIndex = 1, Content = "Chunk B" },
        };
        var embs = new[] { new float[] { 1, 0 }, new float[] { 0, 1 } };
        var infos = new ChunkWriteInfo[]
        {
            new() { EnrichedText = "Chunk A", Status = ChunkIndexStatus.Indexed, IsContextualized = true },
            new() { EnrichedText = "Chunk B", Status = ChunkIndexStatus.Indexed, IsContextualized = true },
        };
        var fileInfo = new FileWriteInfo { FileHash = "h1", Status = FileIndexStatus.Ready, ChunksRaw = 0, ChunksPending = 0 };
        await store.ReplaceFileChunksAsync("data.pdf", chunks, embs, "test", infos, fileInfo);

        // Mark as failed — should report success (row updated).
        var updated = await store.MarkFileAsFailedAsync("data.pdf", FileIndexStatus.NeedsAction, "OCR unavailable", "parse");
        Assert.IsTrue(updated, "MarkFileAsFailedAsync should return true when a file_index row was updated.");

        // Chunks preserved
        Assert.IsNotNull(await store.GetChunkAsync("mf_0"));
        Assert.IsNotNull(await store.GetChunkAsync("mf_1"));
        Assert.AreEqual(2, await store.GetTotalChunkCountAsync());

        // file_index updated
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, last_error, last_error_stage FROM file_index WHERE source_path = 'data.pdf'";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual((int)FileIndexStatus.NeedsAction, reader.GetInt32(0));
        Assert.AreEqual("OCR unavailable", reader.GetString(1));
        Assert.AreEqual("parse", reader.GetString(2));
    }

    [TestMethod]
    public async Task MarkFileAsFailed_NoExistingFile_ReturnsFalse()
    {
        // Replaces the legacy MarkFileAsFailed_NoExistingFile_DoesNotThrow test,
        // which silently documented a latent bug: MarkFileAsFailedAsync is a
        // pure UPDATE with no INSERT fallback, so a file that was never indexed
        // could not be marked failed, and the caller had no way to detect the
        // no-op. v1.4.2 changes the return type to Task<bool> so the caller
        // (IndexingEngine's FileExtractionException catch) can log a warning
        // that the file will surface as "added" in check_changes instead.
        using var store = new SqliteVectorStore(CreateTempDb());

        var updated = await store.MarkFileAsFailedAsync(
            "nonexistent.txt", FileIndexStatus.Failed, "parse error", "parse");

        Assert.IsFalse(updated, "MarkFileAsFailedAsync must return false for a file with no existing file_index row.");

        // And the row should not have been created (UPDATE did not fall back to INSERT).
        var hash = await store.GetFileHashAsync("nonexistent.txt");
        Assert.IsNull(hash);
    }

    [TestMethod]
    public async Task CountFilesByStatus_ReflectsFileIndexState()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        // Seed three files in different statuses via PersistChunksAsPendingAsync
        // (PartiallyDeferred) and ReplaceFileChunksAsync (Ready + Degraded).
        await store.PersistChunksAsPendingAsync(
            "deferred-1.pdf",
            new[] { new DocumentChunk { Id = "d1_0", SourcePath = "deferred-1.pdf", ChunkIndex = 0, Content = "x" } },
            new[] { new ChunkWriteInfo { EnrichedText = "x", Status = ChunkIndexStatus.PendingEmbedding, IsContextualized = true } },
            new FileWriteInfo { FileHash = "h_d1", Status = FileIndexStatus.PartiallyDeferred, ChunksRaw = 0, ChunksPending = 1 });

        await store.PersistChunksAsPendingAsync(
            "deferred-2.pdf",
            new[] { new DocumentChunk { Id = "d2_0", SourcePath = "deferred-2.pdf", ChunkIndex = 0, Content = "y" } },
            new[] { new ChunkWriteInfo { EnrichedText = "y", Status = ChunkIndexStatus.PendingEmbedding, IsContextualized = true } },
            new FileWriteInfo { FileHash = "h_d2", Status = FileIndexStatus.PartiallyDeferred, ChunksRaw = 0, ChunksPending = 1 });

        await store.ReplaceFileChunksAsync(
            "ready.txt",
            new[] { new DocumentChunk { Id = "r_0", SourcePath = "ready.txt", ChunkIndex = 0, Content = "z" } },
            new[] { new float[] { 1f } },
            "test",
            new[] { new ChunkWriteInfo { EnrichedText = "z", Status = ChunkIndexStatus.Indexed, IsContextualized = true } },
            new FileWriteInfo { FileHash = "h_r", Status = FileIndexStatus.Ready, ChunksRaw = 0, ChunksPending = 0 });

        Assert.AreEqual(2, await store.CountFilesByStatusAsync(FileIndexStatus.PartiallyDeferred));
        Assert.AreEqual(1, await store.CountFilesByStatusAsync(FileIndexStatus.Ready));
        Assert.AreEqual(0, await store.CountFilesByStatusAsync(FileIndexStatus.Failed));
        Assert.AreEqual(0, await store.CountFilesByStatusAsync(FileIndexStatus.Degraded));
        Assert.AreEqual(0, await store.CountFilesByStatusAsync(FileIndexStatus.NeedsAction));
    }

    [TestMethod]
    public async Task CountFilesByStatus_EmptyDb_ReturnsZero()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        Assert.AreEqual(0, await store.CountFilesByStatusAsync(FileIndexStatus.Ready));
        Assert.AreEqual(0, await store.CountFilesByStatusAsync(FileIndexStatus.PartiallyDeferred));
        Assert.AreEqual(0, await store.CountFilesByStatusAsync(FileIndexStatus.Failed));
    }

    [TestMethod]
    public async Task GetPendingEmbeddingChunks_ReturnsOnlyPendingStatus()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        // Insert chunks with different statuses via ReplaceFileChunksAsync
        var chunks = new[]
        {
            new DocumentChunk { Id = "pe_0", SourcePath = "mix.txt", ChunkIndex = 0, Content = "Indexed" },
            new DocumentChunk { Id = "pe_1", SourcePath = "mix.txt", ChunkIndex = 1, Content = "Raw" },
            new DocumentChunk { Id = "pe_2", SourcePath = "mix.txt", ChunkIndex = 2, Content = "Pending" },
        };
        var embs = new[] { new float[] { 1, 0 }, new float[] { 0, 1 }, new float[] { 1, 1 } };
        var infos = new ChunkWriteInfo[]
        {
            new() { EnrichedText = "Indexed", Status = ChunkIndexStatus.Indexed, IsContextualized = true },
            new() { EnrichedText = "Raw", Status = ChunkIndexStatus.IndexedRaw, IsContextualized = false },
            new() { EnrichedText = "Pending enriched", Status = ChunkIndexStatus.PendingEmbedding, IsContextualized = true },
        };
        await store.ReplaceFileChunksAsync("mix.txt", chunks, embs, "test", infos);

        var pending = await store.GetPendingEmbeddingChunksAsync();

        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual("pe_2", pending[0].Id);
        Assert.AreEqual("Pending enriched", pending[0].EnrichedText);
    }

    [TestMethod]
    public async Task GetPendingEmbeddingChunks_OrderByRetryCount()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        // Insert pending chunks, then manually set retry_count via raw SQL
        var chunks = new[]
        {
            new DocumentChunk { Id = "rc_0", SourcePath = "a.txt", ChunkIndex = 0, Content = "A" },
            new DocumentChunk { Id = "rc_1", SourcePath = "a.txt", ChunkIndex = 1, Content = "B" },
        };
        var embs = new[] { new float[] { 1, 0 }, new float[] { 0, 1 } };
        var infos = new ChunkWriteInfo[]
        {
            new() { EnrichedText = "A", Status = ChunkIndexStatus.PendingEmbedding, IsContextualized = true },
            new() { EnrichedText = "B", Status = ChunkIndexStatus.PendingEmbedding, IsContextualized = true },
        };
        await store.ReplaceFileChunksAsync("a.txt", chunks, embs, "test", infos);

        // Set rc_0 to higher retry count
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chunks SET retry_count = 5 WHERE id = 'rc_0'";
        cmd.ExecuteNonQuery();

        var pending = await store.GetPendingEmbeddingChunksAsync();

        Assert.AreEqual(2, pending.Count);
        Assert.AreEqual("rc_1", pending[0].Id); // lower retry_count first
        Assert.AreEqual("rc_0", pending[1].Id);
    }

    [TestMethod]
    public async Task UpdateChunkStatus_IncrementsRetryCount()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        await store.UpsertChunkAsync(
            new DocumentChunk { Id = "uc_0", SourcePath = "f.txt", ChunkIndex = 0, Content = "Test" },
            new float[] { 1, 0 }, "test");

        await store.UpdateChunkStatusAsync("uc_0", ChunkIndexStatus.PendingEmbedding, "test", lastError: "timeout");
        await store.UpdateChunkStatusAsync("uc_0", ChunkIndexStatus.PendingEmbedding, "test", lastError: "timeout again");

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT retry_count, status, last_error FROM chunks WHERE id = 'uc_0'";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0)); // incremented twice
        Assert.AreEqual((int)ChunkIndexStatus.PendingEmbedding, reader.GetInt32(1));
        Assert.AreEqual("timeout again", reader.GetString(2));
    }

    [TestMethod]
    public async Task UpdateChunkStatus_PendingToIndexed_WritesEmbedding()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        // Insert chunk without a real embedding (PendingEmbedding state)
        var chunks = new[]
        {
            new DocumentChunk { Id = "ei_0", SourcePath = "f.txt", ChunkIndex = 0, Content = "Embed me" },
        };
        var embs = new[] { new float[] { 0, 0 } }; // placeholder
        var infos = new ChunkWriteInfo[]
        {
            new() { EnrichedText = "Embed me enriched", Status = ChunkIndexStatus.PendingEmbedding, IsContextualized = true },
        };
        await store.ReplaceFileChunksAsync("f.txt", chunks, embs, "test", infos);

        // Now retry with real embedding
        var realEmb = new float[] { 0.8f, 0.2f };
        await store.UpdateChunkStatusAsync("ei_0", ChunkIndexStatus.Indexed, "v2-model", realEmb);

        // Verify status updated
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM chunks WHERE id = 'ei_0'";
        Assert.AreEqual((long)(int)ChunkIndexStatus.Indexed, cmd.ExecuteScalar());

        // Verify embedding updated via vector search
        var results = await store.SearchAsync(new float[] { 0.8f, 0.2f }, topK: 1, threshold: 0.9f);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("ei_0", results[0].ChunkId);
    }

    [TestMethod]
    public void Index_PartialOnStatus_Created()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'idx_chunks_status'";
        var result = cmd.ExecuteScalar();
        Assert.IsNotNull(result);
        Assert.AreEqual("idx_chunks_status", result);
    }

    /// <summary>
    /// Seeds a single-chunk file with file_index via ReplaceFileChunksAsync.
    /// Replacement for obsolete SetFileHashAsync in tests.
    /// </summary>
    static async Task SeedFileWithChunkAsync(
        SqliteVectorStore store, string sourcePath, string chunkId, string fileHash,
        string content = "Test content")
    {
        var chunks = new[] { new DocumentChunk { Id = chunkId, SourcePath = sourcePath, ChunkIndex = 0, Content = content } };
        var embs = new[] { new float[] { 1, 0 } };
        var infos = new[] { new ChunkWriteInfo { EnrichedText = content, Status = ChunkIndexStatus.Indexed, IsContextualized = true } };
        var fileInfo = new FileWriteInfo { FileHash = fileHash, Status = FileIndexStatus.Ready, ChunksRaw = 0, ChunksPending = 0 };
        await store.ReplaceFileChunksAsync(sourcePath, chunks, embs, "test", infos, fileInfo);
    }

    // --- Phase 3b: UpdateProgress & Metadata Tests ---

    [TestMethod]
    public void UpdateProgress_NullParameters_PreservesExistingValues()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        // Acquire lock and set initial values with all parameters
        store.AcquireLock(Environment.ProcessId);
        store.UpdateProgress(5, 10, "embedding", 2, ProviderHealth.ContextualizerUnavailable);

        // Update with only positional params — new columns should be preserved
        store.UpdateProgress(6, 10);

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current, total, current_stage, failed_count, provider_health FROM _indexing_lock WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(6, reader.GetInt32(0));    // current updated
        Assert.AreEqual(10, reader.GetInt32(1));   // total updated
        Assert.AreEqual("embedding", reader.GetString(2)); // preserved
        Assert.AreEqual(2, reader.GetInt32(3));    // preserved
        Assert.AreEqual((int)ProviderHealth.ContextualizerUnavailable, reader.GetInt32(4)); // preserved
    }

    [TestMethod]
    public void UpdateProgress_NewParameters_PersistsValues()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        store.AcquireLock(Environment.ProcessId);
        store.UpdateProgress(3, 10, "embedding", 1, ProviderHealth.EmbeddingUnavailable);

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_stage, failed_count, provider_health FROM _indexing_lock WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("embedding", reader.GetString(0));
        Assert.AreEqual(1, reader.GetInt32(1));
        Assert.AreEqual((int)ProviderHealth.EmbeddingUnavailable, reader.GetInt32(2));
    }

    #region user_version sentinel

    [TestMethod]
    public void GetUserVersion_FreshDb_ReturnsTargetVersion()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        Assert.AreEqual(SqliteVectorStore.TargetUserVersion, store.GetUserVersion());
    }

    [TestMethod]
    public void GetUserVersion_FreshDb_SqlitePragmaMatches()
    {
        var dbPath = CreateTempDb();
        using (var store = new SqliteVectorStore(dbPath)) { }

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var raw = Convert.ToInt32(cmd.ExecuteScalar());

        Assert.AreEqual(SqliteVectorStore.TargetUserVersion, raw);
    }

    [TestMethod]
    public void ReadUserVersion_FreshDb_ReturnsTargetVersion()
    {
        var dbPath = CreateTempDb();
        using (var store = new SqliteVectorStore(dbPath)) { }

        Assert.AreEqual(SqliteVectorStore.TargetUserVersion, SqliteVectorStore.ReadUserVersion(dbPath));
    }

    [TestMethod]
    public void ReadUserVersion_LegacyUntaggedDb_ReturnsZero()
    {
        // Simulate a legacy DB: create it via raw SQLite without InitializeSchema()
        // running, so user_version is never set. Matches what every KB looked like
        // before v1.4.1 landed.
        var dir = Path.Combine(Path.GetTempPath(), "rag_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "legacy.db");

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(connStr))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Minimal table so the file is a valid SQLite DB but untagged.
            cmd.CommandText = "CREATE TABLE marker (x INTEGER);";
            cmd.ExecuteNonQuery();
        }

        Assert.AreEqual(0, SqliteVectorStore.ReadUserVersion(dbPath));
    }

    [TestMethod]
    public void GetUserVersion_ReopenExistingDb_StillReturnsTargetVersion()
    {
        var dbPath = CreateTempDb();
        using (var store = new SqliteVectorStore(dbPath)) { }

        // Re-opening an already-initialized DB should re-run InitializeSchema
        // (idempotent) and leave user_version at TargetUserVersion.
        using var reopened = new SqliteVectorStore(dbPath);
        Assert.AreEqual(SqliteVectorStore.TargetUserVersion, reopened.GetUserVersion());
    }

    [TestMethod]
    public void OpenReadOnly_DoesNotChangeUserVersion()
    {
        // Legacy DB — no user_version tag.
        var dir = Path.Combine(Path.GetTempPath(), "rag_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "legacy.db");
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(connStr))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE marker (x INTEGER);";
            cmd.ExecuteNonQuery();
        }

        // Opening read-only must never run InitializeSchema, so user_version
        // stays at 0 even after GetUserVersion() is called. This is the core
        // "serve = reader" invariant in unit-test form.
        using (var roStore = new SqliteVectorStore(dbPath, readOnly: true))
        {
            Assert.AreEqual(0, roStore.GetUserVersion());
        }

        Assert.AreEqual(0, SqliteVectorStore.ReadUserVersion(dbPath));
    }

    #endregion

    #region v1.4.2 Task 1 — PersistChunksAsPendingAsync (Commit 1)

    static DocumentChunk Chunk(string id, string sourcePath, int index, string content) =>
        new()
        {
            Id = id,
            SourcePath = sourcePath,
            ChunkIndex = index,
            Content = content,
            CharOffset = 0,
            Metadata = "{}",
        };

    static ChunkWriteInfo PendingInfo(string enriched) =>
        new()
        {
            EnrichedText = enriched,
            Status = ChunkIndexStatus.PendingEmbedding,
            IsContextualized = true,
        };

    static FileWriteInfo PendingFileInfo(string hash, int pending) =>
        new()
        {
            FileHash = hash,
            Status = FileIndexStatus.PartiallyDeferred,
            ChunksRaw = 0,
            ChunksPending = pending,
        };

    [TestMethod]
    public async Task PersistChunksAsPending_WritesChunksWithPendingStatus()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var chunks = new[]
        {
            Chunk("p_0", "big.pdf", 0, "page one"),
            Chunk("p_1", "big.pdf", 1, "page two"),
            Chunk("p_2", "big.pdf", 2, "page three"),
        };
        var infos = new[]
        {
            PendingInfo("page one enriched"),
            PendingInfo("page two enriched"),
            PendingInfo("page three enriched"),
        };

        await store.PersistChunksAsPendingAsync(
            "big.pdf", chunks, infos, PendingFileInfo("hash1", 3));

        // All three chunks present with PendingEmbedding status.
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using var chunkCmd = conn.CreateCommand();
        chunkCmd.CommandText = "SELECT id, status, retry_count, enriched FROM chunks WHERE source_path = 'big.pdf' ORDER BY chunk_index";
        using var reader = chunkCmd.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            Assert.AreEqual($"p_{count}", reader.GetString(0));
            Assert.AreEqual((int)ChunkIndexStatus.PendingEmbedding, reader.GetInt32(1));
            Assert.AreEqual(0, reader.GetInt32(2));
            Assert.AreEqual($"page {new[] { "one", "two", "three" }[count]} enriched", reader.GetString(3));
            count++;
        }
        reader.Close();
        Assert.AreEqual(3, count);
    }

    [TestMethod]
    public async Task PersistChunksAsPending_DoesNotWriteEmbeddings()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var chunks = new[] { Chunk("ne_0", "doc.txt", 0, "hello") };
        var infos = new[] { PendingInfo("hello enriched") };
        await store.PersistChunksAsPendingAsync(
            "doc.txt", chunks, infos, PendingFileInfo("h1", 1));

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM embeddings WHERE chunk_id = 'ne_0'";
        Assert.AreEqual(0L, cmd.ExecuteScalar());
    }

    [TestMethod]
    public async Task PersistChunksAsPending_PopulatesFtsForBm25()
    {
        // Pending chunks must remain searchable via BM25 — per v1.4.2 Decision G
        // (degraded mode allowed). Vector search naturally skips them because no
        // embeddings row exists, but keyword search should still find them.
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var chunks = new[] { Chunk("fts_0", "doc.md", 0, "quantum electrodynamics textbook") };
        var infos = new[] { PendingInfo("quantum electrodynamics textbook enriched") };
        await store.PersistChunksAsPendingAsync(
            "doc.md", chunks, infos, PendingFileInfo("h1", 1));

        var ftsResults = await store.SearchFtsAsync("electrodynamics", topK: 5);
        Assert.AreEqual(1, ftsResults.Count, "FTS should find the pending chunk.");
        Assert.AreEqual("fts_0", ftsResults[0].ChunkId);
    }

    [TestMethod]
    public async Task PersistChunksAsPending_UpsertsFileIndexAsPartiallyDeferred()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var chunks = new[] { Chunk("fi_0", "report.docx", 0, "body") };
        var infos = new[] { PendingInfo("body enriched") };
        await store.PersistChunksAsPendingAsync(
            "report.docx", chunks, infos, PendingFileInfo("hash_abc", 1));

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT file_hash, status, chunks_pending
            FROM file_index WHERE source_path = 'report.docx'
            """;
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("hash_abc", reader.GetString(0));
        Assert.AreEqual((int)FileIndexStatus.PartiallyDeferred, reader.GetInt32(1));
        Assert.AreEqual(1, reader.GetInt32(2));
    }

    [TestMethod]
    public async Task PersistChunksAsPending_ReplacesExistingChunks()
    {
        // A second call for the same source path must delete the old chunks
        // (via DeleteBySourcePathAsync inside the transaction) and insert the
        // new ones. This is the "file was modified, re-extract" path.
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var v1Chunks = new[] { Chunk("r_0", "same.pdf", 0, "v1 content") };
        var v1Infos = new[] { PendingInfo("v1 content enriched") };
        await store.PersistChunksAsPendingAsync("same.pdf", v1Chunks, v1Infos, PendingFileInfo("hash_v1", 1));

        // Replace with two new chunks (different ids).
        var v2Chunks = new[]
        {
            Chunk("r_new_0", "same.pdf", 0, "v2 part one"),
            Chunk("r_new_1", "same.pdf", 1, "v2 part two"),
        };
        var v2Infos = new[]
        {
            PendingInfo("v2 part one enriched"),
            PendingInfo("v2 part two enriched"),
        };
        await store.PersistChunksAsPendingAsync("same.pdf", v2Chunks, v2Infos, PendingFileInfo("hash_v2", 2));

        // Old chunk must be gone, new chunks must be present.
        Assert.IsNull(await store.GetChunkAsync("r_0"));
        Assert.IsNotNull(await store.GetChunkAsync("r_new_0"));
        Assert.IsNotNull(await store.GetChunkAsync("r_new_1"));

        // file_index should reflect the new hash and pending count.
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_hash, chunks_pending FROM file_index WHERE source_path = 'same.pdf'";
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("hash_v2", reader.GetString(0));
        Assert.AreEqual(2, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task PersistChunksAsPending_EmptyChunks_IsNoOp()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        // Must not throw, must not create a file_index row.
        await store.PersistChunksAsPendingAsync(
            "empty.txt",
            Array.Empty<DocumentChunk>(),
            Array.Empty<ChunkWriteInfo>(),
            PendingFileInfo("h", 0));

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM file_index WHERE source_path = 'empty.txt'";
        Assert.AreEqual(0L, cmd.ExecuteScalar());
    }

    [TestMethod]
    public async Task PersistChunksAsPending_MismatchedCounts_Throws()
    {
        using var store = new SqliteVectorStore(CreateTempDb());

        var chunks = new[] { Chunk("x_0", "doc.txt", 0, "a"), Chunk("x_1", "doc.txt", 1, "b") };
        var infos = new[] { PendingInfo("a") };   // count mismatch

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            store.PersistChunksAsPendingAsync("doc.txt", chunks, infos, PendingFileInfo("h", 2)));
    }

    #endregion

    #region v1.4.2 Task 2 — PromoteChunksToIndexedAsync (Commit 2a)

    [TestMethod]
    public async Task PromoteChunksToIndexed_TransitionsStatusAndInsertsEmbeddings()
    {
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        // Seed via Commit 1.
        var chunks = new[]
        {
            Chunk("pr_0", "doc.txt", 0, "alpha"),
            Chunk("pr_1", "doc.txt", 1, "beta"),
        };
        var infos = new[] { PendingInfo("alpha"), PendingInfo("beta") };
        await store.PersistChunksAsPendingAsync("doc.txt", chunks, infos, PendingFileInfo("h1", 2));

        // Promote via Commit 2a.
        var embeddings = new[] { new float[] { 1f, 0f }, new float[] { 0f, 1f } };
        await store.PromoteChunksToIndexedAsync(
            "doc.txt",
            chunkIds: new[] { "pr_0", "pr_1" },
            embeddings: embeddings,
            modelId: "test-model");

        // chunks.status becomes Indexed, retry_count stays 0, last_error cleared.
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var chunkCmd = conn.CreateCommand();
        chunkCmd.CommandText = """
            SELECT id, status, retry_count, last_error
            FROM chunks WHERE source_path = 'doc.txt' ORDER BY chunk_index
            """;
        using var chunkReader = chunkCmd.ExecuteReader();
        var seen = 0;
        while (chunkReader.Read())
        {
            Assert.AreEqual($"pr_{seen}", chunkReader.GetString(0));
            Assert.AreEqual((int)ChunkIndexStatus.Indexed, chunkReader.GetInt32(1));
            Assert.AreEqual(0, chunkReader.GetInt32(2));
            Assert.IsTrue(chunkReader.IsDBNull(3));
            seen++;
        }
        chunkReader.Close();
        Assert.AreEqual(2, seen);

        // embeddings table now has rows for both chunks.
        using var embCmd = conn.CreateCommand();
        embCmd.CommandText = "SELECT COUNT(*) FROM embeddings WHERE chunk_id IN ('pr_0','pr_1')";
        Assert.AreEqual(2L, embCmd.ExecuteScalar());

        // file_index status → Ready.
        using var fileCmd = conn.CreateCommand();
        fileCmd.CommandText = "SELECT status, chunks_pending FROM file_index WHERE source_path = 'doc.txt'";
        using var fileReader = fileCmd.ExecuteReader();
        Assert.IsTrue(fileReader.Read());
        Assert.AreEqual((int)FileIndexStatus.Ready, fileReader.GetInt32(0));
        Assert.AreEqual(0, fileReader.GetInt32(1));
    }

    [TestMethod]
    public async Task PromoteChunksToIndexed_PartialPromotion_LeavesRemainingPending()
    {
        // Simulate batch splitting landing in v1.4.3: half the chunks promoted,
        // half remain PendingEmbedding. file_index should reflect Degraded or
        // PartiallyDeferred and chunks_pending should be the remaining count.
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var chunks = new[]
        {
            Chunk("pp_0", "big.pdf", 0, "A"),
            Chunk("pp_1", "big.pdf", 1, "B"),
            Chunk("pp_2", "big.pdf", 2, "C"),
            Chunk("pp_3", "big.pdf", 3, "D"),
        };
        var infos = new[]
        {
            PendingInfo("A"), PendingInfo("B"), PendingInfo("C"), PendingInfo("D"),
        };
        await store.PersistChunksAsPendingAsync("big.pdf", chunks, infos, PendingFileInfo("big_h", 4));

        // Promote only the first two chunks.
        await store.PromoteChunksToIndexedAsync(
            "big.pdf",
            chunkIds: new[] { "pp_0", "pp_1" },
            embeddings: new[] { new float[] { 1f, 0f }, new float[] { 0f, 1f } },
            modelId: "m",
            fileStatus: FileIndexStatus.PartiallyDeferred,
            chunksPending: 2);

        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        // First two chunks are Indexed, last two stay PendingEmbedding.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM chunks WHERE source_path = 'big.pdf' ORDER BY chunk_index";
        using var reader = cmd.ExecuteReader();
        var statuses = new List<int>();
        while (reader.Read()) statuses.Add(reader.GetInt32(0));
        reader.Close();
        CollectionAssert.AreEqual(
            new[]
            {
                (int)ChunkIndexStatus.Indexed,
                (int)ChunkIndexStatus.Indexed,
                (int)ChunkIndexStatus.PendingEmbedding,
                (int)ChunkIndexStatus.PendingEmbedding,
            },
            statuses);

        // file_index: status = PartiallyDeferred, chunks_pending = 2.
        using var fileCmd = conn.CreateCommand();
        fileCmd.CommandText = "SELECT status, chunks_pending FROM file_index WHERE source_path = 'big.pdf'";
        using var fileReader = fileCmd.ExecuteReader();
        Assert.IsTrue(fileReader.Read());
        Assert.AreEqual((int)FileIndexStatus.PartiallyDeferred, fileReader.GetInt32(0));
        Assert.AreEqual(2, fileReader.GetInt32(1));
    }

    [TestMethod]
    public async Task PromoteChunksToIndexed_EmptyList_IsNoOp()
    {
        using var store = new SqliteVectorStore(CreateTempDb());
        await store.PromoteChunksToIndexedAsync(
            "nothing.txt", Array.Empty<string>(), Array.Empty<float[]>(), "m");
        // No exception, no file_index row created.
    }

    [TestMethod]
    public async Task PromoteChunksToIndexed_MismatchedCounts_Throws()
    {
        using var store = new SqliteVectorStore(CreateTempDb());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            store.PromoteChunksToIndexedAsync(
                "x.txt",
                chunkIds: new[] { "a", "b" },
                embeddings: new[] { new float[] { 1f } },   // mismatch
                modelId: "m"));
    }

    [TestMethod]
    public async Task PersistThenPromote_VectorSearchFindsPromotedChunks()
    {
        // End-to-end invariant: PendingEmbedding chunks are invisible to vector
        // search; after promotion they become findable.
        var dbPath = CreateTempDb();
        using var store = new SqliteVectorStore(dbPath);

        var chunks = new[] { Chunk("vs_0", "doc.txt", 0, "searchable") };
        var infos = new[] { PendingInfo("searchable") };
        await store.PersistChunksAsPendingAsync("doc.txt", chunks, infos, PendingFileInfo("h", 1));

        // Before promotion: vector search returns nothing (no embeddings row).
        var before = await store.SearchAsync(new float[] { 1f, 0f }, topK: 5, threshold: 0f);
        Assert.AreEqual(0, before.Count);

        // Promote.
        await store.PromoteChunksToIndexedAsync(
            "doc.txt",
            chunkIds: new[] { "vs_0" },
            embeddings: new[] { new float[] { 1f, 0f } },
            modelId: "m");

        // After promotion: vector search finds the chunk.
        var after = await store.SearchAsync(new float[] { 1f, 0f }, topK: 5, threshold: 0f);
        Assert.AreEqual(1, after.Count);
        Assert.AreEqual("vs_0", after[0].ChunkId);
    }

    #endregion
}
