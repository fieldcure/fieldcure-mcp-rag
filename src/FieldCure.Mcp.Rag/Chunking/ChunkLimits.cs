namespace FieldCure.Mcp.Rag.Chunking;

/// <summary>
/// Character-based chunk size bounds. Chosen to stay safely within the
/// token limit of virtually all current embedding models without requiring
/// per-model tuning or tokenizer libraries.
/// </summary>
public static class ChunkLimits
{
    /// <summary>
    /// Conservative default max chars. Roughly 2,000 tokens for Latin
    /// scripts and 1,500 for CJK — safe for 2048+ token models.
    /// </summary>
    public const int DefaultMaxChars = 4000;

    /// <summary>Minimum chunk size. Never split smaller than this.</summary>
    public const int MinChars = 200;
}
