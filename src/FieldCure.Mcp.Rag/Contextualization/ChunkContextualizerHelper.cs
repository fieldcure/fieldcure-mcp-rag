using System.Text;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// Shared prompt building and response parsing logic for chunk contextualizers.
/// </summary>
internal static class ChunkContextualizerHelper
{
    internal const string SystemPrompt =
        """
        You are a document indexing assistant. For each text chunk, you produce two things:
        1. CONTEXT: A concise 1-2 sentence description of where this chunk sits within the document and what it covers. Include the document subject, section topic, and any entities (company names, product names, dates) that are not explicitly stated in the chunk.
        2. KEYWORDS: A list of normalized keywords extracted from the chunk. Remove grammatical suffixes (particles, verb endings, case markers) to produce base forms. Include both the original language terms and English equivalents for technical terms.

        Respond in exactly this format:
        CONTEXT: <context text>
        KEYWORDS: <comma-separated keywords>
        """;

    internal const int FullDocThreshold = 4_000;
    internal const int TruncatedDocSize = 2_000;
    internal const int LargeDocThreshold = 20_000;

    internal static string BuildPrompt(
        string chunkText,
        string? documentContext,
        string sourceFileName,
        int chunkIndex,
        int totalChunks)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Source: {sourceFileName} (chunk {chunkIndex + 1} of {totalChunks})");

        if (!string.IsNullOrEmpty(documentContext))
        {
            sb.AppendLine();
            sb.AppendLine("<document_summary>");
            sb.AppendLine(documentContext);
            sb.AppendLine("</document_summary>");
        }

        sb.AppendLine();
        sb.AppendLine("<chunk>");
        sb.AppendLine(chunkText);
        sb.AppendLine("</chunk>");

        return sb.ToString();
    }

    internal static string ParseEnrichedOutput(string aiOutput, string originalChunk)
    {
        var contextLine = "";
        var keywordsLine = "";

        foreach (var line in aiOutput.Split('\n'))
        {
            if (line.StartsWith("CONTEXT:", StringComparison.OrdinalIgnoreCase))
                contextLine = line["CONTEXT:".Length..].Trim();
            else if (line.StartsWith("KEYWORDS:", StringComparison.OrdinalIgnoreCase))
                keywordsLine = line["KEYWORDS:".Length..].Trim();
        }

        // If AI returned nothing useful, just return original
        if (string.IsNullOrEmpty(contextLine) && string.IsNullOrEmpty(keywordsLine))
            return originalChunk;

        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(contextLine))
            sb.AppendLine(contextLine);

        if (!string.IsNullOrEmpty(keywordsLine))
            sb.AppendLine($"Keywords: {keywordsLine}");

        sb.AppendLine();
        sb.Append(originalChunk);

        return sb.ToString();
    }

    internal static string? TruncateDocumentContext(string fullText)
    {
        if (string.IsNullOrEmpty(fullText))
            return null;

        if (fullText.Length <= FullDocThreshold)
            return fullText;

        if (fullText.Length <= LargeDocThreshold)
            return fullText[..TruncatedDocSize] + "\n...\n" + fullText[^TruncatedDocSize..];

        return fullText[..TruncatedDocSize] + "\n...(truncated)";
    }
}
