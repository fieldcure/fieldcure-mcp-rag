using FieldCure.Mcp.Rag.Models;

namespace FieldCure.Mcp.Rag.Storage;

/// <summary>
/// Snapshot of a file_index row used by hash-skip decisions in the indexing
/// engine. Returned by <see cref="SqliteVectorStore.GetFileStateAsync"/>.
/// </summary>
/// <param name="Hash">SHA-256 hex hash of the file content at the last indexing attempt.</param>
/// <param name="Status">Current <see cref="FileIndexStatus"/> of the file_index row.</param>
public sealed record FileState(string Hash, FileIndexStatus Status);
