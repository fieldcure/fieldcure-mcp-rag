namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Thrown when content extraction fails (parse error, OCR needed, unsupported format).
/// Wraps the underlying parser exception to make stage-1 failures distinguishable.
/// </summary>
public sealed class FileExtractionException : Exception
{
    /// <summary>
    /// Gets the file path whose extraction failed.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Initializes a new extraction failure for the specified source file.
    /// </summary>
    /// <param name="filePath">Source file path that failed extraction.</param>
    /// <param name="message">User-facing failure message.</param>
    /// <param name="inner">Optional underlying parser exception.</param>
    public FileExtractionException(string filePath, string message, Exception? inner = null)
        : base(message, inner)
    {
        FilePath = filePath;
    }
}
