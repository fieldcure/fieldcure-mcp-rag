namespace FieldCure.Mcp.Rag.Indexing;

/// <summary>
/// Thrown when content extraction fails (parse error, OCR needed, unsupported format).
/// Wraps the underlying parser exception to make stage-1 failures distinguishable.
/// </summary>
public sealed class FileExtractionException : Exception
{
    public string FilePath { get; }

    public FileExtractionException(string filePath, string message, Exception? inner = null)
        : base(message, inner)
    {
        FilePath = filePath;
    }
}
