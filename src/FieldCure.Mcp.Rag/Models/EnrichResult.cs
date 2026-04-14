namespace FieldCure.Mcp.Rag.Models;

/// <summary>
/// Result of a chunk contextualization attempt.
/// Contextualization failure is non-fatal — the original chunk text is returned
/// so indexing can proceed, but <see cref="IsContextualized"/>=false signals degraded quality
/// to allow callers to track and surface the state.
/// </summary>
public sealed record EnrichResult
{
    /// <summary>The text to use for indexing — either enriched or the original chunk text on failure.</summary>
    public required string Text { get; init; }

    /// <summary>True if contextualization succeeded; false if the original text was returned as fallback.</summary>
    public required bool IsContextualized { get; init; }

    /// <summary>Failure reason when <see cref="IsContextualized"/> is false. Null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Exception type name when <see cref="IsContextualized"/> is false. Null on success.</summary>
    public string? FailureType { get; init; }

    /// <summary>Creates a successful enrichment result.</summary>
    public static EnrichResult Success(string enrichedText) =>
        new() { Text = enrichedText, IsContextualized = true };

    /// <summary>Creates a failed enrichment result that falls back to the original text.</summary>
    public static EnrichResult Failed(string originalText, Exception ex) => new()
    {
        Text = originalText,
        IsContextualized = false,
        FailureReason = ex.Message,
        FailureType = ex.GetType().Name,
    };
}
