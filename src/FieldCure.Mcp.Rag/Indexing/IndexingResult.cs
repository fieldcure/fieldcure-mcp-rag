namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Detailed result of an indexing run, replacing the previous <c>int</c> exit code.
/// </summary>
public sealed record IndexingResult
{
    /// <summary>Number of files fully indexed (includes degraded files).</summary>
    public required int Indexed { get; init; }

    /// <summary>Number of files skipped (unchanged hash, empty text, or zero chunks).</summary>
    public required int Skipped { get; init; }

    /// <summary>Number of files that failed (extraction or unexpected errors).</summary>
    public required int Failed { get; init; }

    /// <summary>Number of files indexed but with some chunks missing contextualization.</summary>
    public required int Degraded { get; init; }

    /// <summary>Number of files where embedding failed; previous data preserved.</summary>
    public required int PartiallyDeferred { get; init; }

    /// <summary>List of failed file paths with error messages.</summary>
    public required IReadOnlyList<FailedFile> FailedFiles { get; init; }

    /// <summary>Total duration of the indexing run.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Process exit code derived from the run outcome.
    /// 0 = success (including degraded/deferred), 1 = some files failed, 2 = cancelled.
    /// </summary>
    public required int ExitCode { get; init; }
}

/// <summary>
/// A file that failed during indexing, with path and error message.
/// </summary>
public sealed record FailedFile(string Path, string Reason);
