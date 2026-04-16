using System.Text.RegularExpressions;

namespace FieldCure.Mcp.Rag.Chunking;

/// <summary>
/// Splits document text into overlapping chunks using a sliding window approach.
/// Respects sentence boundaries for Korean, English, and CJK text.
/// </summary>
public sealed partial class TextChunker
{
    readonly int _chunkSize;
    readonly int _overlap;
    readonly int _maxChars;

    /// <summary>
    /// Sentence boundary pattern covering:
    /// - Korean sentence endings: 다. 요. 죠. 까. 군. 네. 나. 지. etc.
    /// - English: word followed by period and whitespace (not decimal numbers)
    /// - CJK period (。)
    /// - Paragraph breaks (\n\n+)
    /// </summary>
    [GeneratedRegex(
        @"(?<=[다요죠까군네나지]\.)\s+|" +        // Korean: 습니다. 해요. 하죠.
        @"(?<=습니다\.)\s+|" +                    // 합니다. (formal)
        @"(?<=(?<!\d)\.(?!\d))\s+(?=[A-Z가-힣])|" + // English/Korean: period then space then capital/한글
        @"(?<=。)\s*(?=\S)|" +                    // CJK period
        @"\n{2,}")]                               // Paragraph break
    private static partial Regex SentenceBoundary();

    /// <param name="chunkSize">Target chunk size in characters (default: 1000).</param>
    /// <param name="overlap">Overlap between adjacent chunks in characters (default: 150).</param>
    /// <param name="maxChars">Hard upper bound per chunk. Chunks exceeding this are re-split.</param>
    public TextChunker(int chunkSize = 1000, int overlap = 150, int maxChars = 0)
    {
        _chunkSize = chunkSize;
        _overlap = overlap;
        _maxChars = maxChars > 0 ? maxChars : ChunkLimits.DefaultMaxChars;
    }

    /// <summary>
    /// Splits text into chunks, preserving sentence boundaries.
    /// Returns list of (content, charOffset) tuples.
    /// </summary>
    public IReadOnlyList<(string Content, int CharOffset)> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Split text into sentences first
        var sentences = SplitIntoSentences(text);
        var results = new List<(string Content, int CharOffset)>();
        var minSize = (int)(_chunkSize * 0.3);

        var currentChunk = "";
        var currentOffset = 0;

        foreach (var (sentence, offset) in sentences)
        {
            if (currentChunk.Length == 0)
            {
                currentChunk = sentence;
                currentOffset = offset;
                continue;
            }

            if (currentChunk.Length + sentence.Length + 1 <= _chunkSize)
            {
                // Sentence fits in current chunk
                currentChunk = currentChunk + " " + sentence;
            }
            else
            {
                // Current chunk is full, emit it
                results.Add((currentChunk.Trim(), currentOffset));

                // Start new chunk with overlap from end of previous
                if (_overlap > 0 && currentChunk.Length > _overlap)
                {
                    var overlapText = currentChunk[^_overlap..];
                    // Find sentence boundary in overlap to avoid cutting mid-sentence
                    var overlapStart = overlapText.IndexOf(' ');
                    if (overlapStart >= 0)
                        overlapText = overlapText[(overlapStart + 1)..];
                    currentChunk = overlapText + " " + sentence;
                    currentOffset = offset - overlapText.Length;
                }
                else
                {
                    currentChunk = sentence;
                    currentOffset = offset;
                }
            }
        }

        // Emit remaining content
        if (currentChunk.Trim().Length > 0)
        {
            var trimmed = currentChunk.Trim();
            if (trimmed.Length < minSize && results.Count > 0)
            {
                // Merge short trailing chunk with previous
                var (prevContent, prevCharOffset) = results[^1];
                results[^1] = (prevContent + " " + trimmed, prevCharOffset);
            }
            else
            {
                results.Add((trimmed, currentOffset));
            }
        }

        return EnforceMaxChars(results);
    }

    /// <summary>
    /// Re-splits any chunk exceeding <see cref="_maxChars"/>.
    /// Tries sentence boundaries first, then forces a hard cut.
    /// </summary>
    List<(string Content, int CharOffset)> EnforceMaxChars(
        List<(string Content, int CharOffset)> chunks)
    {
        var enforced = new List<(string Content, int CharOffset)>(chunks.Count);

        foreach (var (content, offset) in chunks)
        {
            if (content.Length <= _maxChars)
            {
                enforced.Add((content, offset));
                continue;
            }

            SplitOversized(content, offset, enforced);
        }

        return enforced;
    }

    /// <summary>
    /// Recursively splits an oversized chunk: sentence boundary → hard cut.
    /// </summary>
    void SplitOversized(string text, int baseOffset, List<(string Content, int CharOffset)> output)
    {
        if (text.Length <= _maxChars)
        {
            if (text.Trim().Length > 0)
                output.Add((text.Trim(), baseOffset));
            return;
        }

        // Try splitting on sentence boundary within the allowed range
        var sentences = SplitIntoSentences(text);
        if (sentences.Count > 1)
        {
            var current = "";
            var currentOffset = baseOffset;
            foreach (var (sentence, sentenceOffset) in sentences)
            {
                if (current.Length == 0)
                {
                    current = sentence;
                    currentOffset = baseOffset + sentenceOffset;
                    continue;
                }

                if (current.Length + sentence.Length + 1 <= _maxChars)
                {
                    current = current + " " + sentence;
                }
                else
                {
                    SplitOversized(current, currentOffset, output);
                    current = sentence;
                    currentOffset = baseOffset + sentenceOffset;
                }
            }

            if (current.Trim().Length > 0)
                SplitOversized(current, currentOffset, output);

            return;
        }

        // No sentence boundaries — hard cut at maxChars
        var pos = 0;
        while (pos < text.Length)
        {
            var remaining = text.Length - pos;
            var take = Math.Min(remaining, _maxChars);
            var fragment = text.Substring(pos, take).Trim();
            if (fragment.Length > 0)
                output.Add((fragment, baseOffset + pos));
            pos += take;
        }
    }

    /// <summary>
    /// Splits text into sentences using the sentence boundary regex.
    /// Preserves character offsets.
    /// </summary>
    static List<(string Content, int Offset)> SplitIntoSentences(string text)
    {
        var sentences = new List<(string Content, int Offset)>();
        var lastEnd = 0;

        foreach (Match match in SentenceBoundary().Matches(text))
        {
            var sentenceEnd = match.Index;
            if (sentenceEnd > lastEnd)
            {
                var content = text[lastEnd..sentenceEnd].Trim();
                if (content.Length > 0)
                {
                    // Check if we'd be splitting inside parentheses/quotes
                    if (!IsInsideParentheses(text, sentenceEnd))
                    {
                        sentences.Add((content, lastEnd));
                        lastEnd = match.Index + match.Length;
                        continue;
                    }
                }
            }
            // If inside parentheses, skip this boundary
        }

        // Add remaining text
        if (lastEnd < text.Length)
        {
            var remaining = text[lastEnd..].Trim();
            if (remaining.Length > 0)
                sentences.Add((remaining, lastEnd));
        }

        // If no sentence boundaries found, return entire text
        if (sentences.Count == 0 && text.Trim().Length > 0)
            sentences.Add((text.Trim(), 0));

        return sentences;
    }

    /// <summary>
    /// Checks if the position is inside unmatched parentheses or quotes.
    /// Prevents splitting inside expressions like "Gosea et al. 2023)" or "Ph.D. Stanford)".
    /// </summary>
    static bool IsInsideParentheses(string text, int position)
    {
        var depth = 0;
        for (var i = 0; i < position && i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    if (depth > 0) depth--;
                    break;
            }
        }
        return depth > 0;
    }
}
