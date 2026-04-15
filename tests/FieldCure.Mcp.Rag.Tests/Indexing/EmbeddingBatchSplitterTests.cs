using FieldCure.Mcp.Rag.Embedding;
using FieldCure.Mcp.Rag.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldCure.Mcp.Rag.Tests.Indexing;

[TestClass]
public class EmbeddingBatchSplitterTests
{
    [TestMethod]
    public async Task HappyPath_AllChunksSucceed_OneProviderCall()
    {
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: _ => false,
            dimension: 4);

        var chunkIds = new[] { "a", "b", "c", "d" };
        var texts = new[] { "text-a", "text-b", "text-c", "text-d" };

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, NullLogger.Instance, chunkIds, texts, CancellationToken.None);

        Assert.IsFalse(result.DeferredFallback);
        Assert.AreEqual(4, result.Succeeded.Count);
        Assert.AreEqual(0, result.FailedChunkIds.Count);
        Assert.AreEqual(1, provider.CallCount);

        CollectionAssert.AreEqual(
            chunkIds, result.Succeeded.Select(s => s.ChunkId).ToArray());
    }

    [TestMethod]
    public async Task EmptyInput_ReturnsEmptyResult_NoProviderCall()
    {
        var provider = new FakeEmbeddingProvider(_ => false, 4);

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, NullLogger.Instance, [], [], CancellationToken.None);

        Assert.IsFalse(result.DeferredFallback);
        Assert.AreEqual(0, result.Succeeded.Count);
        Assert.AreEqual(0, result.FailedChunkIds.Count);
        Assert.AreEqual(0, provider.CallCount);
    }

    [TestMethod]
    public async Task SingleBadChunkInBatch_IsolatedAsFailed_RestSucceed()
    {
        // Reject the text "BAD" — batches containing it throw.
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: texts => texts.Any(t => t == "BAD"),
            dimension: 4);

        var chunkIds = new[] { "a", "b", "c", "d" };
        var texts = new[] { "ok-a", "BAD", "ok-c", "ok-d" };

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, NullLogger.Instance, chunkIds, texts, CancellationToken.None);

        Assert.IsFalse(result.DeferredFallback);
        Assert.AreEqual(3, result.Succeeded.Count);
        Assert.AreEqual(1, result.FailedChunkIds.Count);
        Assert.AreEqual("b", result.FailedChunkIds[0]);

        var succeededIds = result.Succeeded.Select(s => s.ChunkId).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(new[] { "a", "c", "d" }, succeededIds);
    }

    [TestMethod]
    public async Task AllChunksRejected_TriggersDeferredFallbackViaRatioGuard()
    {
        // Every batch is rejected. At depth 0, >50% failure → deferred.
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: _ => true,
            dimension: 4);

        var chunkIds = new[] { "a", "b", "c", "d" };
        var texts = new[] { "t1", "t2", "t3", "t4" };

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, NullLogger.Instance, chunkIds, texts, CancellationToken.None);

        Assert.IsTrue(result.DeferredFallback);
        Assert.AreEqual(0, result.Succeeded.Count);
        Assert.AreEqual(0, result.FailedChunkIds.Count);
    }

    [TestMethod]
    public async Task SingleChunkRejected_BaseCaseFires_NotDeferred()
    {
        // A top-level size-1 batch goes straight to the base case
        // (count == 1) and returns Failed without reaching the ratio
        // guard. The caller marks the chunk Failed and the file is
        // Degraded rather than deferred — legitimate per-chunk failure.
        var provider = new FakeEmbeddingProvider(_ => true, 4);

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, NullLogger.Instance,
            new[] { "only" }, new[] { "only-text" },
            CancellationToken.None);

        Assert.IsFalse(result.DeferredFallback);
        Assert.AreEqual(0, result.Succeeded.Count);
        Assert.AreEqual(1, result.FailedChunkIds.Count);
        Assert.AreEqual("only", result.FailedChunkIds[0]);
    }

    [TestMethod]
    public async Task OddSizedBatch_SplitsCorrectlyAtMidpoint()
    {
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: texts => texts.Any(t => t == "BAD"),
            dimension: 4);

        var chunkIds = new[] { "a", "b", "c", "d", "e" };
        var texts = new[] { "ok", "ok", "BAD", "ok", "ok" };

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, NullLogger.Instance, chunkIds, texts, CancellationToken.None);

        Assert.IsFalse(result.DeferredFallback);
        Assert.AreEqual(4, result.Succeeded.Count);
        Assert.AreEqual(1, result.FailedChunkIds.Count);
        Assert.AreEqual("c", result.FailedChunkIds[0]);
    }

    [TestMethod]
    public async Task MismatchedCounts_ThrowsArgumentException()
    {
        var provider = new FakeEmbeddingProvider(_ => false, 4);

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
                provider, NullLogger.Instance,
                new[] { "a", "b" }, new[] { "text" },
                CancellationToken.None);
        });
    }

    [TestMethod]
    public async Task CancellationDuringEmbed_Rethrows()
    {
        var cts = new CancellationTokenSource();
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
            dimension: 4);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
                provider, NullLogger.Instance,
                new[] { "a" }, new[] { "text" },
                cts.Token);
        });
    }

    [TestMethod]
    public async Task TwoChunksOneBad_IsolatedCorrectly()
    {
        // Minimal recursion: 2 → 1+1.
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: texts => texts.Any(t => t == "BAD"),
            dimension: 4);

        var chunkIds = new[] { "good", "bad" };
        var texts = new[] { "ok", "BAD" };

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, NullLogger.Instance, chunkIds, texts, CancellationToken.None);

        // 1 failed out of 2 = 50%, NOT strictly > 50%, so no deferred.
        Assert.IsFalse(result.DeferredFallback);
        Assert.AreEqual(1, result.Succeeded.Count);
        Assert.AreEqual("good", result.Succeeded[0].ChunkId);
        Assert.AreEqual(1, result.FailedChunkIds.Count);
        Assert.AreEqual("bad", result.FailedChunkIds[0]);
    }

    [TestMethod]
    public async Task UnauthorizedFromProvider_BubblesUp_NotSwallowed()
    {
        // 401 is auth failure — must not be converted into per-chunk
        // rejections. The caller (deferred retry pass) needs it to flag
        // ProviderHealth and break.
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: _ => throw new HttpRequestException(
                "Unauthorized",
                inner: null,
                statusCode: System.Net.HttpStatusCode.Unauthorized),
            dimension: 4);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
        {
            await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
                provider, NullLogger.Instance,
                new[] { "a", "b" }, new[] { "t1", "t2" },
                CancellationToken.None);
        });
    }

    [TestMethod]
    public async Task ForbiddenFromProvider_BubblesUp_NotSwallowed()
    {
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: _ => throw new HttpRequestException(
                "Forbidden",
                inner: null,
                statusCode: System.Net.HttpStatusCode.Forbidden),
            dimension: 4);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
        {
            await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
                provider, NullLogger.Instance,
                new[] { "a" }, new[] { "t1" },
                CancellationToken.None);
        });
    }

    [TestMethod]
    public async Task HappyPath_EmitsNoLogMessages()
    {
        // Happy path must be absolutely silent — no Info/Debug/Warning.
        // This protects normal runs from log spam.
        var provider = new FakeEmbeddingProvider(_ => false, 4);
        var logger = new RecordingLogger();

        var chunkIds = new[] { "a", "b", "c", "d" };
        var texts = new[] { "t1", "t2", "t3", "t4" };

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, logger, chunkIds, texts, CancellationToken.None,
            sourceLabel: "happy.pdf");

        Assert.IsFalse(result.DeferredFallback);
        Assert.AreEqual(4, result.Succeeded.Count);
        Assert.AreEqual(0, logger.Records.Count,
            "Happy path must emit no log messages — got: " +
            string.Join(", ", logger.Records.Select(r => r.Message)));
    }

    [TestMethod]
    public async Task SplitMode_EmitsStartAndDoneWithDiagnostics()
    {
        // One bad chunk → split mode. Verify the trace frames are emitted:
        // start (Info), done (Info) with providerCalls + depthMax, and the
        // sourcePath label appears in both.
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: texts => texts.Any(t => t == "BAD"),
            dimension: 4);
        var logger = new RecordingLogger();

        var chunkIds = new[] { "a", "b", "c", "d" };
        var texts = new[] { "ok", "BAD", "ok", "ok" };

        var result = await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, logger, chunkIds, texts, CancellationToken.None,
            sourceLabel: "traced.pdf");

        Assert.AreEqual(3, result.Succeeded.Count);
        Assert.AreEqual(1, result.FailedChunkIds.Count);

        var infoMessages = logger.Records
            .Where(r => r.Level == LogLevel.Information)
            .Select(r => r.Message)
            .ToArray();

        Assert.IsTrue(
            infoMessages.Any(m => m.Contains("[BinarySplit] start")
                && m.Contains("sourcePath=\"traced.pdf\"")
                && m.Contains("chunks=4")),
            "Expected start line with sourcePath and chunk count — got: " +
            string.Join(" | ", infoMessages));

        Assert.IsTrue(
            infoMessages.Any(m => m.Contains("[BinarySplit] done")
                && m.Contains("promoted=3")
                && m.Contains("failed=1")
                && m.Contains("fallback=False")
                && m.Contains("providerCalls=")
                && m.Contains("depthMax=")),
            "Expected done line with diagnostics — got: " +
            string.Join(" | ", infoMessages));

        // Terminal per-chunk failure is Warning-level.
        var warnings = logger.Records
            .Where(r => r.Level == LogLevel.Warning)
            .Select(r => r.Message)
            .ToArray();
        Assert.IsTrue(
            warnings.Any(m => m.Contains("size=1 FAILED") && m.Contains("chunk b")),
            "Expected Warning for size=1 chunk rejection — got: " +
            string.Join(" | ", warnings));
    }

    [TestMethod]
    public async Task SplitMode_UsesAbsoluteRangeOffsets_NotLocalIndices()
    {
        // The bad chunk at absolute index 2 should appear as range=[2..2]
        // in the Warning line — not [0..0] from some sub-batch's local view.
        var provider = new FakeEmbeddingProvider(
            rejectPredicate: texts => texts.Any(t => t == "BAD"),
            dimension: 4);
        var logger = new RecordingLogger();

        var chunkIds = new[] { "c0", "c1", "c2", "c3" };
        var texts = new[] { "ok", "ok", "BAD", "ok" };

        await EmbeddingBatchSplitter.EmbedWithBinarySplitAsync(
            provider, logger, chunkIds, texts, CancellationToken.None);

        var sizeOneWarning = logger.Records
            .Where(r => r.Level == LogLevel.Warning)
            .Select(r => r.Message)
            .FirstOrDefault(m => m.Contains("size=1 FAILED"));

        Assert.IsNotNull(sizeOneWarning);
        Assert.IsTrue(
            sizeOneWarning.Contains("range=[2..2]"),
            "Expected absolute range=[2..2] for chunk at index 2 — got: " + sizeOneWarning);
    }

    #region Test doubles

    sealed record LogRecord(LogLevel Level, string Message);

    sealed class RecordingLogger : ILogger
    {
        public List<LogRecord> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add(new LogRecord(logLevel, formatter(state, exception)));
        }
    }

    sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        readonly Func<IReadOnlyList<string>, bool> _rejectPredicate;

        public FakeEmbeddingProvider(
            Func<IReadOnlyList<string>, bool> rejectPredicate,
            int dimension)
        {
            _rejectPredicate = rejectPredicate;
            Dimension = dimension;
        }

        public int Dimension { get; }
        public string ModelId => "fake";
        public int CallCount { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => throw new NotImplementedException("Not used by splitter.");

        public Task<float[][]> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            CallCount++;
            if (_rejectPredicate(texts))
                throw new HttpRequestException(
                    $"fake rejection for batch of {texts.Count}");

            var result = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
                result[i] = new float[Dimension];
            return Task.FromResult(result);
        }
    }

    #endregion
}
