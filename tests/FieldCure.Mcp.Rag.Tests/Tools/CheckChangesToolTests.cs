using System.Security.Cryptography;
using System.Text.Json;
using FieldCure.Mcp.Rag.Configuration;

using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Storage;
using FieldCure.Mcp.Rag.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag.Tests.Tools;

[TestClass]
public class CheckChangesToolTests
{
    sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimension => 2;
        public string ModelId => "stub";

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new float[] { 1f, 0f });
    }

    static IEmbeddingProvider StubEmbedding(ProviderConfig cfg)
        => new StubEmbeddingProvider();

    static string CreateBasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_checkchanges_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Creates a KB with a tagged (v1.4.1) rag.db and one indexed file that
    /// matches the on-disk source. Returns the base path and kb id.
    /// </summary>
    static async Task<(string BasePath, string KbId)> CreateCleanKbAsync(string kbId = "kb-test")
    {
        var basePath = CreateBasePath();

        var sourceDir = Path.Combine(basePath, "sources");
        Directory.CreateDirectory(sourceDir);
        var testFile = Path.Combine(sourceDir, "doc.txt");
        await File.WriteAllTextAsync(testFile, "Hello world");
        var hash = ComputeSha256(testFile);

        var kbDir = Path.Combine(basePath, kbId);
        Directory.CreateDirectory(kbDir);

        var config = new RagConfig
        {
            Id = kbId,
            Name = "Test KB",
            SourcePaths = new List<string> { sourceDir },
            Embedding = new ProviderConfig { Provider = "openai", Model = "text-embedding-3-small" },
        };
        await File.WriteAllTextAsync(
            Path.Combine(kbDir, "config.json"),
            JsonSerializer.Serialize(config, McpJson.Config));

        var dbPath = Path.Combine(kbDir, "rag.db");
        using (var store = new SqliteVectorStore(dbPath))
        {
            var chunk = new DocumentChunk
            {
                Id = "c0",
                SourcePath = "doc.txt",
                ChunkIndex = 0,
                Content = "Hello world",
                CharOffset = 0,
                Metadata = "{}",
            };
            var chunkInfo = new ChunkWriteInfo
            {
                EnrichedText = "Hello world",
                Status = ChunkIndexStatus.Indexed,
                IsContextualized = true,
            };
            var fileInfo = new FileWriteInfo
            {
                FileHash = hash,
                Status = FileIndexStatus.Ready,
                ChunksRaw = 1,
                ChunksPending = 0,
            };
            await store.ReplaceFileChunksAsync(
                "doc.txt",
                new[] { chunk },
                new[] { new float[] { 1f, 0f } },
                "test-model",
                new[] { chunkInfo },
                fileInfo);
        }

        return (basePath, kbId);
    }

    [TestMethod]
    public async Task CheckChanges_CleanTaggedKb_ReportsIsCleanAndCurrentSchema()
    {
        var (basePath, kbId) = await CreateCleanKbAsync();

        using var ctx = new MultiKbContext(basePath, StubEmbedding);
        var json = await CheckChangesTool.CheckChanges(ctx, NullLogger<MultiKbContext>.Instance, kbId);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual(0, root.GetProperty("added").GetInt32());
        Assert.AreEqual(0, root.GetProperty("modified").GetInt32());
        Assert.AreEqual(0, root.GetProperty("deleted").GetInt32());
        Assert.IsFalse(root.GetProperty("is_schema_stale").GetBoolean());
        Assert.AreEqual(SqliteVectorStore.TargetUserVersion, root.GetProperty("kb_schema_version").GetInt32());
        Assert.AreEqual(SqliteVectorStore.TargetUserVersion, root.GetProperty("current_schema_version").GetInt32());
        Assert.IsTrue(root.GetProperty("is_clean").GetBoolean());
    }

    [TestMethod]
    public async Task CheckChanges_LegacyUntaggedDb_ReportsSchemaStaleAndNotClean()
    {
        var basePath = CreateBasePath();

        var sourceDir = Path.Combine(basePath, "sources");
        Directory.CreateDirectory(sourceDir);
        var testFile = Path.Combine(sourceDir, "doc.txt");
        await File.WriteAllTextAsync(testFile, "Legacy content");
        var hash = ComputeSha256(testFile);

        var kbId = "kb-legacy";
        var kbDir = Path.Combine(basePath, kbId);
        Directory.CreateDirectory(kbDir);

        var config = new RagConfig
        {
            Id = kbId,
            Name = "Legacy KB",
            SourcePaths = new List<string> { sourceDir },
            Embedding = new ProviderConfig { Provider = "openai", Model = "text-embedding-3-small" },
        };
        await File.WriteAllTextAsync(
            Path.Combine(kbDir, "config.json"),
            JsonSerializer.Serialize(config, McpJson.Config));

        // Create a legacy DB: manually build a v1.3-ish schema without going
        // through InitializeSchema (so user_version stays 0). Only the tables
        // CheckChangesTool reads are needed: file_index, index_metadata, chunks.
        var dbPath = Path.Combine(kbDir, "rag.db");
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(connStr))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE chunks (
                    id TEXT PRIMARY KEY,
                    source_path TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    enriched TEXT,
                    char_offset INTEGER NOT NULL DEFAULT 0,
                    metadata TEXT NOT NULL DEFAULT '{}'
                );
                CREATE TABLE file_index (
                    source_path TEXT PRIMARY KEY,
                    file_hash TEXT NOT NULL,
                    indexed_at TEXT NOT NULL
                );
                CREATE TABLE index_metadata (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE _indexing_lock (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    pid INTEGER NOT NULL,
                    started TEXT NOT NULL,
                    current INTEGER NOT NULL DEFAULT 0,
                    total INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO file_index (source_path, file_hash, indexed_at)
                    VALUES ('doc.txt', @hash, '2026-01-01T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.ExecuteNonQuery();
        }

        // Sanity: the legacy DB is actually untagged.
        Assert.AreEqual(0, SqliteVectorStore.ReadUserVersion(dbPath));

        using var ctx = new MultiKbContext(basePath, StubEmbedding);
        var json = await CheckChangesTool.CheckChanges(ctx, NullLogger<MultiKbContext>.Instance, kbId);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual(0, root.GetProperty("added").GetInt32());
        Assert.AreEqual(0, root.GetProperty("modified").GetInt32());
        Assert.AreEqual(0, root.GetProperty("deleted").GetInt32());
        Assert.IsTrue(root.GetProperty("is_schema_stale").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("kb_schema_version").GetInt32());
        Assert.AreEqual(SqliteVectorStore.TargetUserVersion, root.GetProperty("current_schema_version").GetInt32());
        Assert.IsFalse(root.GetProperty("is_clean").GetBoolean(),
            "is_clean must flip to false when schema is stale even if no files changed.");

        // Confirm the legacy DB was not mutated — user_version stays 0.
        // This is the "serve = reader" invariant at the tool level.
        Assert.AreEqual(0, SqliteVectorStore.ReadUserVersion(dbPath));
    }

    [TestMethod]
    public async Task CheckChanges_FileModified_ReportsModifiedAndNotClean()
    {
        var (basePath, kbId) = await CreateCleanKbAsync();

        // Modify the source file after indexing.
        var sourceFile = Path.Combine(basePath, "sources", "doc.txt");
        await File.WriteAllTextAsync(sourceFile, "Hello world — updated");

        using var ctx = new MultiKbContext(basePath, StubEmbedding);
        var json = await CheckChangesTool.CheckChanges(ctx, NullLogger<MultiKbContext>.Instance, kbId);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual(1, root.GetProperty("modified").GetInt32());
        Assert.IsFalse(root.GetProperty("is_schema_stale").GetBoolean());
        Assert.IsFalse(root.GetProperty("is_clean").GetBoolean());
    }
}
