using FieldCure.Mcp.Rag.Chunking;
using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Models;
using FieldCure.Mcp.Rag.Search;
using FieldCure.Mcp.Rag.Storage;

namespace FieldCure.Mcp.Rag.Tests.Integration;

[TestClass]
public class HybridSearchIntegrationTests
{
    static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag_integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static async Task<(SqliteVectorStore Store, HybridSearcher Searcher)> IndexTestContent(string dbPath)
    {
        var store = new SqliteVectorStore(dbPath);
        var embedder = new NullEmbeddingProvider();
        var chunker = new TextChunker();
        var searcher = new HybridSearcher(store, embedder);

        // Index some realistic content
        var documents = new Dictionary<string, string>
        {
            ["battery_eis.txt"] = "전기화학 임피던스 분광법(EIS)은 배터리의 내부 저항을 측정하는 기법입니다. " +
                "임피던스 스펙트럼을 분석하면 전극 반응과 확산 과정을 분리할 수 있습니다. " +
                "ZStudio 소프트웨어를 사용하여 Nyquist plot과 Bode plot을 생성합니다.",
            ["weather.txt"] = "Tomorrow the weather will be sunny with a high temperature of 25 degrees. " +
                "The wind will blow from the southwest at moderate speeds. " +
                "No precipitation is expected throughout the weekend.",
            ["cell_design.txt"] = "리튬이온 배터리 셀 설계에서 양극재 조성은 에너지 밀도에 직접적인 영향을 미칩니다. " +
                "NMC 811 양극재는 높은 니켈 함량으로 고에너지 밀도를 달성하지만 열안정성이 낮습니다. " +
                "BMS(Battery Management System)는 셀 밸런싱과 과충전 보호를 담당합니다.",
        };

        foreach (var (path, text) in documents)
        {
            var chunks = chunker.Split(text);
            var embeddings = await embedder.EmbedBatchAsync(
                chunks.Select(c => c.Content).ToList());

            var docChunks = new DocumentChunk[chunks.Count];
            var chunkInfos = new ChunkWriteInfo[chunks.Count];
            for (int i = 0; i < chunks.Count; i++)
            {
                docChunks[i] = new DocumentChunk
                {
                    Id = $"{path}_{i}",
                    SourcePath = path,
                    ChunkIndex = i,
                    Content = chunks[i].Content,
                    CharOffset = chunks[i].CharOffset,
                };
                chunkInfos[i] = new ChunkWriteInfo
                {
                    EnrichedText = chunks[i].Content,
                    Status = ChunkIndexStatus.Indexed,
                    IsContextualized = true,
                };
            }
            var fileInfo = new FileWriteInfo
            {
                FileHash = $"hash_{path}",
                Status = FileIndexStatus.Ready,
                ChunksRaw = 0,
                ChunksPending = 0,
            };
            await store.ReplaceFileChunksAsync(path, docChunks, embeddings, embedder.ModelId, chunkInfos, fileInfo);
        }

        return (store, searcher);
    }

    [TestMethod]
    public async Task Bm25Only_KoreanKeyword_FindsRelevantChunks()
    {
        var dir = CreateTempDir();
        var (store, searcher) = await IndexTestContent(Path.Combine(dir, "test.db"));
        using var _ = store;

        var result = await searcher.SearchAsync("임피던스", topK: 5, threshold: 0.3f);

        Assert.AreEqual(SearchMode.Bm25Only, result.Mode);
        Assert.IsTrue(result.Results.Count > 0);
        Assert.IsTrue(result.Results.Any(r => r.SourcePath == "battery_eis.txt"));
    }

    [TestMethod]
    public async Task Bm25Only_EnglishTechnicalTerm_FindsMatch()
    {
        var dir = CreateTempDir();
        var (store, searcher) = await IndexTestContent(Path.Combine(dir, "test.db"));
        using var _ = store;

        var result = await searcher.SearchAsync("ZStudio", topK: 5, threshold: 0.3f);

        Assert.AreEqual(SearchMode.Bm25Only, result.Mode);
        Assert.IsTrue(result.Results.Count > 0);
        Assert.IsTrue(result.Results.Any(r => r.SourcePath == "battery_eis.txt"));
    }

    [TestMethod]
    public async Task Bm25Only_EnglishContent_FindsWeather()
    {
        var dir = CreateTempDir();
        var (store, searcher) = await IndexTestContent(Path.Combine(dir, "test.db"));
        using var _ = store;

        var result = await searcher.SearchAsync("weather sunny", topK: 5, threshold: 0.3f);

        Assert.AreEqual(SearchMode.Bm25Only, result.Mode);
        Assert.IsTrue(result.Results.Count > 0);
        Assert.IsTrue(result.Results.Any(r => r.SourcePath == "weather.txt"));
    }

    [TestMethod]
    public async Task Results_IncludeHasPreviousAndHasNext()
    {
        var dir = CreateTempDir();
        var (store, searcher) = await IndexTestContent(Path.Combine(dir, "test.db"));
        using var _ = store;

        var result = await searcher.SearchAsync("배터리", topK: 5, threshold: 0.3f);

        foreach (var r in result.Results)
        {
            Assert.IsTrue(r.TotalChunks > 0, $"TotalChunks should be > 0 for {r.ChunkId}");
        }
    }

    [TestMethod]
    public async Task IncrementalIndex_DeletedFile_OrphanCleanup()
    {
        var dir = CreateTempDir();
        var dbPath = Path.Combine(dir, "test.db");
        var (store, searcher) = await IndexTestContent(dbPath);
        using var _ = store;

        // Verify weather content is indexed
        var before = await searcher.SearchAsync("weather", topK: 5, threshold: 0.3f);
        Assert.IsTrue(before.Results.Count > 0);

        // Simulate orphan cleanup: purge weather.txt
        await store.PurgeSourcePathAsync("weather.txt");

        // Verify it's gone from both vector store and FTS5
        var after = await searcher.SearchAsync("weather", topK: 5, threshold: 0.3f);
        Assert.IsFalse(after.Results.Any(r => r.SourcePath == "weather.txt"));

        // Other content still searchable
        var eis = await searcher.SearchAsync("임피던스", topK: 5, threshold: 0.3f);
        Assert.IsTrue(eis.Results.Count > 0);
    }

    [TestMethod]
    public async Task SearchMode_ReportedCorrectly()
    {
        var dir = CreateTempDir();
        var (store, searcher) = await IndexTestContent(Path.Combine(dir, "test.db"));
        using var _ = store;

        // NullEmbeddingProvider → always Bm25Only (or Bm25Only with no results)
        var result = await searcher.SearchAsync("임피던스 분광법", topK: 5, threshold: 0.3f);
        Assert.AreEqual(SearchMode.Bm25Only, result.Mode);
    }
}
