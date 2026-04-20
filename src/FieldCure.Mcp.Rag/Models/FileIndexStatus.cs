namespace FieldCure.Mcp.Rag.Models;

/// <summary>
/// Tracks the overall indexing state of a source file.
/// Stored in the <c>file_index.status</c> column as an integer.
/// </summary>
public enum FileIndexStatus
{
    /// <summary>All chunks fully indexed and searchable.</summary>
    Ready = 0,

    /// <summary>Indexed but some chunks lack contextualization (silent LLM failure).</summary>
    Degraded = 1,

    /// <summary>Some chunks deferred (e.g., embedding provider unavailable).</summary>
    PartiallyDeferred = 2,

    /// <summary>Requires user intervention (e.g., unsupported format, configuration issue).</summary>
    NeedsAction = 3,

    /// <summary>All processing attempts failed; see <c>last_error</c> for details.</summary>
    Failed = 4,
}
