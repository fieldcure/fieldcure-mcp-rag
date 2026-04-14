using FieldCure.Mcp.Rag.Contextualization;
using FieldCure.Mcp.Rag.Models;

namespace FieldCure.Mcp.Rag.Tests.Contextualization;

[TestClass]
public class ChunkContextualizerHelperTests
{
    [TestMethod]
    public void BuildPrompt_IncludesSourceAndChunkInfo()
    {
        var result = ChunkContextualizerHelper.BuildPrompt(
            "Test chunk text", "Full document text", "test.docx", 2, 10);

        Assert.IsTrue(result.Contains("Source: test.docx (chunk 3 of 10)"));
        Assert.IsTrue(result.Contains("<chunk>"));
        Assert.IsTrue(result.Contains("Test chunk text"));
        Assert.IsTrue(result.Contains("<document_summary>"));
        Assert.IsTrue(result.Contains("Full document text"));
    }

    [TestMethod]
    public void BuildPrompt_NullDocumentContext_OmitsSummary()
    {
        var result = ChunkContextualizerHelper.BuildPrompt(
            "Test chunk text", null, "test.docx", 0, 1);

        Assert.IsFalse(result.Contains("<document_summary>"));
        Assert.IsTrue(result.Contains("<chunk>"));
        Assert.IsTrue(result.Contains("Test chunk text"));
    }

    [TestMethod]
    public void ParseEnrichedOutput_ValidResponse_CombinesContextKeywordsAndOriginal()
    {
        var aiOutput = """
            CONTEXT: This chunk describes impedance measurement procedures.
            KEYWORDS: impedance, 임피던스, measurement, 측정
            """;
        var original = "임피던스를 측정합니다.";

        var result = ChunkContextualizerHelper.ParseEnrichedOutput(aiOutput, original);

        Assert.IsTrue(result.Contains("This chunk describes impedance measurement procedures."));
        Assert.IsTrue(result.Contains("Keywords: impedance, 임피던스, measurement, 측정"));
        Assert.IsTrue(result.Contains(original));
    }

    [TestMethod]
    public void ParseEnrichedOutput_EmptyResponse_ReturnsOriginal()
    {
        var original = "원본 텍스트";
        var result = ChunkContextualizerHelper.ParseEnrichedOutput("", original);
        Assert.AreEqual(original, result);
    }

    [TestMethod]
    public void ParseEnrichedOutput_NoContextOrKeywords_ReturnsOriginal()
    {
        var original = "원본 텍스트";
        var result = ChunkContextualizerHelper.ParseEnrichedOutput("Some random AI output", original);
        Assert.AreEqual(original, result);
    }

    [TestMethod]
    public void ParseEnrichedOutput_OnlyContext_IncludesContextAndOriginal()
    {
        var aiOutput = "CONTEXT: This is about batteries.";
        var original = "Battery test";

        var result = ChunkContextualizerHelper.ParseEnrichedOutput(aiOutput, original);

        Assert.IsTrue(result.Contains("This is about batteries."));
        Assert.IsTrue(result.Contains(original));
        Assert.IsFalse(result.Contains("Keywords:"));
    }

    [TestMethod]
    public void TruncateDocumentContext_ShortText_ReturnsFullText()
    {
        var text = new string('A', 3000);
        var result = ChunkContextualizerHelper.TruncateDocumentContext(text);
        Assert.AreEqual(text, result);
    }

    [TestMethod]
    public void TruncateDocumentContext_MediumText_ReturnsFrontAndBack()
    {
        var text = new string('A', 10_000);
        var result = ChunkContextualizerHelper.TruncateDocumentContext(text);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("..."));
        Assert.IsTrue(result.Length < text.Length);
        // Should contain front 2000 + separator + back 2000
        Assert.IsTrue(result.StartsWith(new string('A', 2000)));
    }

    [TestMethod]
    public void TruncateDocumentContext_LargeText_ReturnsFrontOnly()
    {
        var text = new string('A', 50_000);
        var result = ChunkContextualizerHelper.TruncateDocumentContext(text);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("(truncated)"));
        Assert.IsTrue(result.StartsWith(new string('A', 2000)));
        // Should not contain back text
        Assert.IsTrue(result.Length < 3000);
    }

    [TestMethod]
    public void TruncateDocumentContext_EmptyText_ReturnsNull()
    {
        Assert.IsNull(ChunkContextualizerHelper.TruncateDocumentContext(""));
    }

    [TestMethod]
    public async Task NullChunkContextualizer_ReturnsOriginalText()
    {
        var contextualizer = new NullChunkContextualizer();
        var result = await contextualizer.EnrichAsync(
            "test chunk", "doc context", "file.txt", 0, 1);
        Assert.AreEqual("test chunk", result.Text);
        Assert.IsTrue(result.IsContextualized);
    }

    // --- EnrichResult Tests ---

    [TestMethod]
    public void EnrichResult_Success_PreservesEnrichedText()
    {
        var result = EnrichResult.Success("enriched text with context and keywords");

        Assert.AreEqual("enriched text with context and keywords", result.Text);
        Assert.IsTrue(result.IsContextualized);
        Assert.IsNull(result.FailureReason);
        Assert.IsNull(result.FailureType);
    }

    [TestMethod]
    public void EnrichResult_Failed_PreservesOriginalText()
    {
        var ex = new HttpRequestException("Connection refused");
        var result = EnrichResult.Failed("original chunk text", ex);

        Assert.AreEqual("original chunk text", result.Text);
        Assert.IsFalse(result.IsContextualized);
        Assert.AreEqual("Connection refused", result.FailureReason);
        Assert.AreEqual("HttpRequestException", result.FailureType);
    }

    // --- AnthropicChunkContextualizer failure Tests ---

    [TestMethod]
    public async Task AnthropicContextualizer_HttpFailure_ReturnsFailedResult()
    {
        // Use a bogus URL that will fail immediately
        var contextualizer = new AnthropicChunkContextualizer(
            apiKey: "fake-key",
            model: "claude-haiku-4-5-20251001",
            baseUrl: "http://localhost:1"); // Port 1: guaranteed connection refused

        var result = await contextualizer.EnrichAsync(
            "test chunk", "doc context", "file.txt", 0, 1);

        Assert.IsFalse(result.IsContextualized);
        Assert.AreEqual("test chunk", result.Text);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsNotNull(result.FailureType);
    }

    [TestMethod]
    public async Task AnthropicContextualizer_Cancellation_PropagatesException()
    {
        var contextualizer = new AnthropicChunkContextualizer(
            apiKey: "fake-key",
            model: "claude-haiku-4-5-20251001",
            baseUrl: "http://localhost:1");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            contextualizer.EnrichAsync(
                "test chunk", "doc context", "file.txt", 0, 1, cts.Token));
    }

    [TestMethod]
    public async Task OpenAiContextualizer_HttpFailure_ReturnsFailedResult()
    {
        var contextualizer = new OpenAiChunkContextualizer(
            baseUrl: "http://localhost:1",
            model: "gpt-4o-mini");

        var result = await contextualizer.EnrichAsync(
            "test chunk", "doc context", "file.txt", 0, 1);

        Assert.IsFalse(result.IsContextualized);
        Assert.AreEqual("test chunk", result.Text);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsNotNull(result.FailureType);
    }

    [TestMethod]
    public async Task OpenAiContextualizer_Cancellation_PropagatesException()
    {
        var contextualizer = new OpenAiChunkContextualizer(
            baseUrl: "http://localhost:1",
            model: "gpt-4o-mini");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            contextualizer.EnrichAsync(
                "test chunk", "doc context", "file.txt", 0, 1, cts.Token));
    }
}
