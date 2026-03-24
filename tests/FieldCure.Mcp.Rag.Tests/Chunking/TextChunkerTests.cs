using FieldCure.Mcp.Rag.Chunking;

namespace FieldCure.Mcp.Rag.Tests.Chunking;

[TestClass]
public class TextChunkerTests
{
    [TestMethod]
    public void Split_EmptyText_ReturnsEmpty()
    {
        var chunker = new TextChunker();
        var result = chunker.Split("");
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Split_WhitespaceOnly_ReturnsEmpty()
    {
        var chunker = new TextChunker();
        var result = chunker.Split("   \n\n  ");
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Split_ShortText_ReturnsSingleChunk()
    {
        var chunker = new TextChunker(chunkSize: 1000);
        var result = chunker.Split("Hello world.");
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Hello world.", result[0].Content);
    }

    [TestMethod]
    public void Split_KoreanSentenceEndings_SplitsCorrectly()
    {
        var chunker = new TextChunker(chunkSize: 50, overlap: 0);
        var text = "배터리의 임피던스를 측정합니다. 이후 결과를 분석합니다.";
        var result = chunker.Split(text);

        // Should keep "합니다." intact (period stays with sentence)
        Assert.IsTrue(result.Count >= 1);
        var allContent = string.Join(" ", result.Select(r => r.Content));
        Assert.IsTrue(allContent.Contains("측정합니다."), $"Content: {allContent}");
    }

    [TestMethod]
    public void Split_DecimalNumbers_DoesNotSplitOnDecimalPoint()
    {
        var chunker = new TextChunker(chunkSize: 50, overlap: 0);
        var text = "정확도는 3.14 수준입니다. 다음 단계로 넘어갑니다.";
        var result = chunker.Split(text);

        // "3.14" should stay intact in one chunk
        var allContent = string.Join(" ", result.Select(r => r.Content));
        Assert.IsTrue(allContent.Contains("3.14"));
    }

    [TestMethod]
    public void Split_InsideParentheses_DoesNotSplit()
    {
        var chunker = new TextChunker(chunkSize: 200, overlap: 0);
        var text = "Loewner 방법(Gosea et al. 2023)을 사용합니다.";
        var result = chunker.Split(text);

        // Should keep parenthetical content together
        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result[0].Content.Contains("Gosea et al. 2023"));
    }

    [TestMethod]
    public void Split_EnglishSentences_SplitsOnPeriodSpace()
    {
        var chunker = new TextChunker(chunkSize: 50, overlap: 0);
        var text = "The battery was tested. Results showed improvement. The end.";
        var result = chunker.Split(text);

        // Period should stay with the sentence
        Assert.IsTrue(result.Count >= 1);
        var allContent = string.Join(" ", result.Select(r => r.Content));
        Assert.IsTrue(allContent.Contains("tested."), $"Content: {allContent}");
    }

    [TestMethod]
    public void Split_ParagraphBreaks_SplitsOnDoubleNewline()
    {
        var chunker = new TextChunker(chunkSize: 100, overlap: 0);
        var text = "First paragraph content here.\n\nSecond paragraph content here.";
        var result = chunker.Split(text);

        Assert.IsTrue(result.Count >= 1);
    }

    [TestMethod]
    public void Split_LongText_ProducesMultipleChunks()
    {
        var chunker = new TextChunker(chunkSize: 100, overlap: 20);
        var text = string.Join(". ", Enumerable.Range(1, 20).Select(i => $"Sentence number {i} with some content"));
        var result = chunker.Split(text);

        Assert.IsTrue(result.Count > 1, $"Expected multiple chunks, got {result.Count}");
    }

    [TestMethod]
    public void Split_CjkPeriod_SplitsCorrectly()
    {
        var chunker = new TextChunker(chunkSize: 50, overlap: 0);
        var text = "最初のテスト文章です。次のテスト文章です。";
        var result = chunker.Split(text);

        Assert.IsTrue(result.Count >= 1);
    }

    [TestMethod]
    public void Split_ShortTrailingChunk_MergedWithPrevious()
    {
        var chunker = new TextChunker(chunkSize: 100, overlap: 0);
        // Create text where last sentence would be very short
        var text = "This is a long sentence that fills up most of the chunk size limit for testing. OK.";
        var result = chunker.Split(text);

        // Short trailing "OK." should be merged
        foreach (var chunk in result)
        {
            // No chunk should be just "OK."
            Assert.IsTrue(chunk.Content.Length > 5 || result.Count == 1);
        }
    }
}
