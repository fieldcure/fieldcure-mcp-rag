namespace FieldCure.Mcp.Rag.Models;

/// <summary>
/// Snapshot of the current file_index state. Queried from the DB, not
/// accumulated in-memory — always reflects the actual state regardless
/// of which exec run produced it.
/// </summary>
public sealed record IndexStateCounters(
    int Failed,
    int Degraded,
    int PartiallyDeferred,
    int NeedsAction);
