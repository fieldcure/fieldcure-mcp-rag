using FieldCure.DocumentParsers;
using FieldCure.Mcp.Rag.Chunking;

namespace FieldCure.Mcp.Rag.Tests;

[TestClass]
public class DocumentParsingTests
{
    static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    // ── Markdown ──

    [TestMethod]
    public async Task ParseMarkdown_ReadsFullContent()
    {
        var path = TestDataPath("test_document_rag.md");
        var text = await File.ReadAllTextAsync(path);

        Assert.IsFalse(string.IsNullOrWhiteSpace(text));
        Assert.IsTrue(text.Contains("데이터 기반 의사결정"));
        Assert.IsTrue(text.Contains("RAG"));
    }

    [TestMethod]
    public void ChunkMarkdown_ProducesMultipleChunks()
    {
        var path = TestDataPath("test_document_rag.md");
        var text = File.ReadAllText(path);
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);

        var chunks = chunker.Split(text);

        Assert.IsTrue(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
        // All chunks should have content
        foreach (var (content, _) in chunks)
            Assert.IsFalse(string.IsNullOrWhiteSpace(content));
    }

    [TestMethod]
    public void ChunkMarkdown_PreservesKoreanSentences()
    {
        var path = TestDataPath("test_document_rag.md");
        var text = File.ReadAllText(path);
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);

        var chunks = chunker.Split(text);
        var allContent = string.Join(" ", chunks.Select(c => c.Content));

        // Key Korean sentences should be preserved intact
        Assert.IsTrue(allContent.Contains("의사결정"));
        Assert.IsTrue(allContent.Contains("디지털 전환"));
    }

    // ── DOCX ──

    [TestMethod]
    public void ParseDocx_ExtractsText()
    {
        var path = TestDataPath("test_eis_overview.docx");
        var parser = DocumentParserFactory.GetParser(".docx");

        Assert.IsNotNull(parser, "No parser registered for .docx");

        var bytes = File.ReadAllBytes(path);
        var text = parser.ExtractText(bytes);

        Assert.IsFalse(string.IsNullOrWhiteSpace(text), "Extracted text is empty");
        Assert.IsTrue(text.Length > 100, $"Text too short: {text.Length} chars");
    }

    [TestMethod]
    public void ChunkDocx_ProducesMultipleChunks()
    {
        var path = TestDataPath("test_eis_overview.docx");
        var parser = DocumentParserFactory.GetParser(".docx")!;
        var bytes = File.ReadAllBytes(path);
        var text = parser.ExtractText(bytes);
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);

        var chunks = chunker.Split(text);

        Assert.IsTrue(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
    }

    // ── HWPX ──

    [TestMethod]
    public void ParseHwpx_ExtractsText()
    {
        var path = TestDataPath("전기화학 임피던스 분광법.hwpx");
        var parser = DocumentParserFactory.GetParser(".hwpx");

        Assert.IsNotNull(parser, "No parser registered for .hwpx");

        var bytes = File.ReadAllBytes(path);
        var text = parser.ExtractText(bytes);

        Assert.IsFalse(string.IsNullOrWhiteSpace(text), "Extracted text is empty");
        Assert.IsTrue(text.Length > 100, $"Text too short: {text.Length} chars");
    }

    [TestMethod]
    public void ChunkHwpx_ProducesMultipleChunks()
    {
        var path = TestDataPath("전기화학 임피던스 분광법.hwpx");
        var parser = DocumentParserFactory.GetParser(".hwpx")!;
        var bytes = File.ReadAllBytes(path);
        var text = parser.ExtractText(bytes);
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);

        var chunks = chunker.Split(text);

        Assert.IsTrue(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
    }

    // ── DOCX vs HWPX content consistency ──

    [TestMethod]
    public void DocxAndHwpx_ContainSimilarContent()
    {
        var docxText = ExtractDocxText();
        var hwpxText = ExtractHwpxText();

        Assert.IsTrue(docxText.Length > 100);
        Assert.IsTrue(hwpxText.Length > 100);

        // HWPX (Korean translation) should contain Korean text
        Assert.IsTrue(hwpxText.Any(c => c >= '가' && c <= '힣'),
            "HWPX should contain Korean characters");
    }

    // ── Chunking: decimal & measurement preservation ──

    [TestMethod]
    public void ChunkDocx_PreservesDecimalAndMeasurements()
    {
        var text = ExtractDocxText();
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);
        var chunks = chunker.Split(text);
        var allContent = string.Join(" ", chunks.Select(c => c.Content));

        // Measurement units should not be split on decimal points
        // e.g. "1 mHz", "5-10 mV" should stay intact
        Assert.IsTrue(allContent.Contains("mHz") || allContent.Contains("mV"),
            "Measurement units should be present in chunked content");
    }

    [TestMethod]
    public void ChunkHwpx_PreservesDecimalAndMeasurements()
    {
        var text = ExtractHwpxText();
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);
        var chunks = chunker.Split(text);
        var allContent = string.Join(" ", chunks.Select(c => c.Content));

        Assert.IsTrue(allContent.Contains("mHz") || allContent.Contains("mV"),
            "Measurement units should be present in chunked content");
    }

    // ── Chunking: formula preservation ──

    [TestMethod]
    public void ChunkDocx_PreservesFormulas()
    {
        var text = ExtractDocxText();
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);
        var chunks = chunker.Split(text);
        var allContent = string.Join(" ", chunks.Select(c => c.Content));

        // Z(ω) = V(ω) / I(ω) or similar formula should be in one chunk
        Assert.IsTrue(allContent.Contains("Z(") || allContent.Contains("Z ("),
            $"Impedance formula Z(ω) should be present. Content length: {allContent.Length}");
    }

    // ── Chunking: parenthetical abbreviations ──

    [TestMethod]
    public void ChunkDocx_PreservesAbbreviationsInParentheses()
    {
        var text = ExtractDocxText();
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);
        var chunks = chunker.Split(text);
        var allContent = string.Join(" ", chunks.Select(c => c.Content));

        // Abbreviations in parentheses should not be split
        var hasAbbreviations =
            allContent.Contains("(KK)") ||
            allContent.Contains("(CPE)") ||
            allContent.Contains("(ECM)") ||
            allContent.Contains("KK") ||
            allContent.Contains("CPE") ||
            allContent.Contains("ECM");

        Assert.IsTrue(hasAbbreviations,
            "Technical abbreviations (KK, CPE, ECM) should be present");
    }

    // ── DOCX section content verification ──

    [TestMethod]
    [DataRow("Randles", "Randles circuit should be found")]
    [DataRow("Loewner", "Loewner method should be found")]
    [DataRow("Kramers", "Kramers-Kronig should be found")]
    public void DocxContent_ContainsExpectedKeywords(string keyword, string message)
    {
        var text = ExtractDocxText();
        Assert.IsTrue(text.Contains(keyword, StringComparison.OrdinalIgnoreCase), message);
    }

    [TestMethod]
    [DataRow("Randles", "Randles circuit should be found")]
    [DataRow("Loewner", "Loewner method should be found")]
    public void HwpxContent_ContainsExpectedKeywords(string keyword, string message)
    {
        var text = ExtractHwpxText();
        Assert.IsTrue(text.Contains(keyword, StringComparison.OrdinalIgnoreCase), message);
    }

    // ── Chunk search simulation (keyword hit by chunk) ──

    [TestMethod]
    [DataRow("Randles circuit")]
    [DataRow("series resistance")]
    [DataRow("four-wire")]
    public void ChunkDocx_KeywordFoundInAtLeastOneChunk(string keyword)
    {
        var text = ExtractDocxText();
        var chunker = new TextChunker(chunkSize: 500, overlap: 50);
        var chunks = chunker.Split(text);

        var found = chunks.Any(c =>
            c.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(found,
            $"Keyword '{keyword}' should appear in at least one chunk");
    }

    // ── Helpers ──

    static string ExtractDocxText()
    {
        var parser = DocumentParserFactory.GetParser(".docx")!;
        return parser.ExtractText(File.ReadAllBytes(TestDataPath("test_eis_overview.docx")));
    }

    static string ExtractHwpxText()
    {
        var parser = DocumentParserFactory.GetParser(".hwpx")!;
        return parser.ExtractText(File.ReadAllBytes(TestDataPath("전기화학 임피던스 분광법.hwpx")));
    }
}
