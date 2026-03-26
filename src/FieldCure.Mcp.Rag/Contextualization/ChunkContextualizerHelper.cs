using System.Security.Cryptography;
using System.Text;

namespace FieldCure.Mcp.Rag.Contextualization;

/// <summary>
/// Shared prompt building and response parsing logic for chunk contextualizers.
/// </summary>
internal static class ChunkContextualizerHelper
{
    /// <summary>Metadata key for user-customized system prompt (null = use built-in default).</summary>
    internal const string MetaKeySystemPrompt = "system_prompt";

    /// <summary>Metadata key for SHA256 hash of the effective prompt used during last indexing.</summary>
    internal const string MetaKeyPromptHash = "effective_prompt_hash";

    /// <summary>
    /// Computes a short SHA256 hash of the given prompt text (first 16 hex chars).
    /// </summary>
    internal static string ComputePromptHash(string prompt)
    {
        var bytes = Encoding.UTF8.GetBytes(prompt);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    internal const string SystemPrompt =
        """
        You are a document indexing assistant. For each text chunk, produce:
        CONTEXT: 1-2 sentences describing what this chunk covers and where it sits
        in the document. Mention entities (companies, products, dates) not in the chunk.
        KEYWORDS: Normalized keywords from the chunk.
        Rules:
        - Remove grammatical suffixes to produce base forms
        - For non-English text: include BOTH original language AND English equivalents
        - Include ALL domain-specific concepts, not just general topics
        Format (strict):
        CONTEXT: <text>
        KEYWORDS: <comma-separated>
        Example:
        CONTEXT: This chunk describes the Q3 sales performance in the Asia-Pacific region from Acme Corp's 2025 annual report.
        KEYWORDS: 매출, revenue, 아시아, Asia-Pacific, 성장률, growth rate, 분기, quarter
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
