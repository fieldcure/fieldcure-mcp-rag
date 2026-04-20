using FieldCure.DocumentParsers.Ocr;

namespace FieldCure.Mcp.Rag;

/// <summary>
/// Lazy wrapper around <see cref="TesseractOcrEngine"/> that defers native
/// library loading until the first scanned PDF page is encountered.
/// On non-Windows platforms, returns empty text for scanned pages instead
/// of crashing — the page is silently skipped and the rest of the PDF
/// is indexed normally.
/// </summary>
internal sealed class LazyOcrEngine : IOcrEngine, IDisposable
{
    private TesseractOcrEngine? _inner;
    private bool _initialized;
    private bool _unavailable;
    private readonly object _lock = new();

    /// <inheritdoc />
    public Task<string> RecognizeAsync(byte[] imageBytes)
    {
        EnsureInitialized();

        if (_unavailable || !OperatingSystem.IsWindows())
            return Task.FromResult(string.Empty);

        return _inner!.RecognizeAsync(imageBytes);
    }

    /// <summary>
    /// Lazily constructs the Tesseract engine on first use and marks it
    /// unavailable when the current platform cannot load the native binaries.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            if (!OperatingSystem.IsWindows())
            {
                _unavailable = true;
                Console.Error.WriteLine(
                    "[RAG] Tesseract OCR is not available on this platform. " +
                    "Scanned PDF pages without a text layer will be skipped.");
                return;
            }

            try
            {
                _inner = new TesseractOcrEngine();
            }
            catch (Exception ex)
            {
                _unavailable = true;
                Console.Error.WriteLine(
                    $"[RAG] Tesseract OCR initialization failed: {ex.Message}. " +
                    "Scanned PDF pages will be skipped.");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            _inner?.Dispose();
    }
}
