using System.Text.Json;
using FieldCure.Mcp.Rag.Configuration;

using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Storage;
using Microsoft.Data.Sqlite;

namespace FieldCure.Mcp.Rag.Tests;

[TestClass]
public class MultiKbContextTests
{
    static IEmbeddingProvider NoEmbedding(ProviderConfig cfg)
        => throw new InvalidOperationException("Embedding factory must not be invoked during ListKbs tests.");

    static string CreateBasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_mkctx_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void CreateKbFolder(string basePath, string folderName, string configId, bool createDb = true)
    {
        var kbDir = Path.Combine(basePath, folderName);
        Directory.CreateDirectory(kbDir);

        var config = new RagConfig
        {
            Id = configId,
            Name = folderName,
            SourcePaths = new List<string>(),
            Embedding = new ProviderConfig { Provider = "openai", Model = "text-embedding-3-small" },
        };
        File.WriteAllText(
            Path.Combine(kbDir, "config.json"),
            JsonSerializer.Serialize(config, McpJson.Config));

        if (createDb)
        {
            var dbPath = Path.Combine(kbDir, "rag.db");
            using var store = new SqliteVectorStore(dbPath);
        }
    }

    static MultiKbContext NewContext(string basePath)
        => new(basePath, NoEmbedding);

    [TestMethod]
    public void ListKbs_EmptyBasePath_ReturnsEmpty()
    {
        var basePath = CreateBasePath();
        using var ctx = NewContext(basePath);

        var result = ctx.ListKbs();

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ListKbs_ValidKb_AppearsInResult()
    {
        var basePath = CreateBasePath();
        CreateKbFolder(basePath, "kb-alpha", "kb-alpha");

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("kb-alpha", result[0].Id);
    }

    [TestMethod]
    public void ListKbs_DotPrefixFolder_IsSkipped()
    {
        var basePath = CreateBasePath();
        CreateKbFolder(basePath, "kb-alpha", "kb-alpha");
        CreateKbFolder(basePath, ".backup-20260415", "kb-alpha");

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("kb-alpha", result[0].Id);
    }

    [TestMethod]
    public void ListKbs_UnderscorePrefixFolder_IsSkipped()
    {
        var basePath = CreateBasePath();
        CreateKbFolder(basePath, "kb-alpha", "kb-alpha");
        CreateKbFolder(basePath, "_tmp-staging", "kb-alpha");

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("kb-alpha", result[0].Id);
    }

    [TestMethod]
    public void ListKbs_FolderWithoutConfigJson_IsSkipped()
    {
        var basePath = CreateBasePath();
        CreateKbFolder(basePath, "kb-alpha", "kb-alpha");
        Directory.CreateDirectory(Path.Combine(basePath, "random-folder"));

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("kb-alpha", result[0].Id);
    }

    [TestMethod]
    public void ListKbs_BrokenConfigJson_IsSkipped()
    {
        var basePath = CreateBasePath();
        CreateKbFolder(basePath, "kb-alpha", "kb-alpha");

        var brokenDir = Path.Combine(basePath, "broken-kb");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, "config.json"), "{ not valid json");

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("kb-alpha", result[0].Id);
    }

    [TestMethod]
    public void ListKbs_IdFolderMismatch_IsSkipped()
    {
        var basePath = CreateBasePath();
        CreateKbFolder(basePath, "kb-alpha", "kb-alpha");
        // Copy-backup scenario: folder name differs from config.Id
        CreateKbFolder(basePath, "kb-alpha-copy", "kb-alpha");

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("kb-alpha", result[0].Id);
    }

    [TestMethod]
    public void ListKbs_IdFolderMatch_IsCaseInsensitive()
    {
        var basePath = CreateBasePath();
        // Folder name uses different casing from config.Id — should still match
        CreateKbFolder(basePath, "KB-Alpha", "kb-alpha");

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("KB-Alpha", result[0].Id);
    }

    [TestMethod]
    public void ListKbs_TaggedDb_ReportsCurrentSchemaVersion()
    {
        var basePath = CreateBasePath();
        CreateKbFolder(basePath, "kb-fresh", "kb-fresh");

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(SqliteVectorStore.TargetUserVersion, result[0].SchemaVersion);
        Assert.IsFalse(result[0].IsSchemaStale);
    }

    [TestMethod]
    public void ListKbs_LegacyUntaggedDb_ReportsStale()
    {
        var basePath = CreateBasePath();
        var kbDir = Path.Combine(basePath, "kb-legacy");
        Directory.CreateDirectory(kbDir);

        var config = new RagConfig
        {
            Id = "kb-legacy",
            Name = "kb-legacy",
            SourcePaths = new List<string>(),
            Embedding = new ProviderConfig { Provider = "openai", Model = "text-embedding-3-small" },
        };
        File.WriteAllText(
            Path.Combine(kbDir, "config.json"),
            JsonSerializer.Serialize(config, McpJson.Config));

        // Create a legacy DB without InitializeSchema running — user_version stays 0.
        var dbPath = Path.Combine(kbDir, "rag.db");
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(connStr))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE file_index (source_path TEXT PRIMARY KEY); CREATE TABLE chunks (id TEXT PRIMARY KEY); CREATE TABLE _indexing_lock (id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }

        using var ctx = NewContext(basePath);
        var result = ctx.ListKbs();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0, result[0].SchemaVersion);
        Assert.IsTrue(result[0].IsSchemaStale);
    }
}
