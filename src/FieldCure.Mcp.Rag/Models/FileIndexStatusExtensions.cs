namespace FieldCure.Mcp.Rag.Models;

/// <summary>
/// Extension methods for <see cref="FileIndexStatus"/> that capture cross-
/// cutting hash-skip decisions in one place so future status additions are
/// forced to make an explicit choice.
/// </summary>
public static class FileIndexStatusExtensions
{
    /// <summary>
    /// Returns <c>true</c> when the indexing engine's hash-skip logic should
    /// skip a file with this status, given matching file hash. Every current
    /// status qualifies (for different reasons):
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="FileIndexStatus.Ready"/> — fully indexed, nothing to do.</description></item>
    ///   <item><description><see cref="FileIndexStatus.Degraded"/> — indexed without contextualization; Commit 2a already promoted.</description></item>
    ///   <item><description><see cref="FileIndexStatus.PartiallyDeferred"/> — Commit 1 persisted upstream work; the deferred retry second pass handles embed.</description></item>
    ///   <item><description><see cref="FileIndexStatus.Failed"/> — retry exhausted; waits for <c>--force</c> or user "retry failed" action.</description></item>
    ///   <item><description><see cref="FileIndexStatus.NeedsAction"/> — extraction blocked; same content cannot recover.</description></item>
    /// </list>
    ///
    /// Defaults to <c>false</c> for any future status so callers are forced
    /// to update this switch deliberately rather than silently inheriting
    /// "skip" behavior.
    /// </summary>
    public static bool ShouldSkipOnHashMatch(this FileIndexStatus status) => status switch
    {
        FileIndexStatus.Ready => true,
        FileIndexStatus.Degraded => true,
        FileIndexStatus.PartiallyDeferred => true,
        FileIndexStatus.Failed => true,
        FileIndexStatus.NeedsAction => true,
        _ => false,
    };
}
