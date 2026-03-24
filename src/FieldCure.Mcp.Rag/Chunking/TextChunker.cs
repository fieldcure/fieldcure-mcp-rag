namespace FieldCure.Mcp.Rag.Chunking;

/// <summary>
/// Splits document text into overlapping chunks using a sliding window approach.
/// Respects sentence boundaries where possible.
/// </summary>
public sealed class TextChunker
{
    readonly int _chunkSize;
    readonly int _overlap;

    static readonly string[] SentenceSeparators = [". ", "\n", "\u3002"];

    /// <param name="chunkSize">Target chunk size in characters (default: 1000).</param>
    /// <param name="overlap">Overlap between adjacent chunks in characters (default: 150).</param>
    public TextChunker(int chunkSize = 1000, int overlap = 150)
    {
        _chunkSize = chunkSize;
        _overlap = overlap;
    }

    /// <summary>
    /// Splits text into chunks, preserving sentence boundaries.
    /// Returns list of (content, charOffset) tuples.
    /// </summary>
    public IReadOnlyList<(string Content, int CharOffset)> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var results = new List<(string Content, int CharOffset)>();
        var minSize = (int)(_chunkSize * 0.3);
        var pos = 0;

        while (pos < text.Length)
        {
            var end = Math.Min(pos + _chunkSize, text.Length);

            // Try to find a sentence boundary near the end of the chunk
            if (end < text.Length)
            {
                var bestBreak = -1;
                foreach (var sep in SentenceSeparators)
                {
                    var searchStart = pos + minSize;
                    if (searchStart >= end)
                        searchStart = pos;

                    var idx = text.LastIndexOf(sep, end - 1, end - searchStart, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var breakAt = idx + sep.Length;
                        if (bestBreak < 0 || breakAt > bestBreak)
                            bestBreak = breakAt;
                    }
                }

                if (bestBreak > pos)
                    end = bestBreak;
            }

            var chunk = text[pos..end].Trim();

            if (chunk.Length >= minSize || results.Count == 0)
            {
                results.Add((chunk, pos));
            }
            else if (results.Count > 0)
            {
                // Merge short trailing chunk with previous
                var prev = results[^1];
                results[^1] = (prev.Content + " " + chunk, prev.CharOffset);
            }

            // Advance with overlap
            var step = end - pos - _overlap;
            if (step <= 0)
                step = Math.Max(1, end - pos);
            pos += step;
        }

        return results;
    }
}
