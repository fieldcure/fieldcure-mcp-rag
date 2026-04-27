using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using FieldCure.DocumentParsers.Audio;
using FieldCure.DocumentParsers.Audio.Transcription;

namespace FieldCure.Mcp.Rag;

/// <summary>
/// Lazy wrapper around <see cref="WhisperTranscriber"/> that defers ggml model
/// download and native Whisper runtime loading until the first audio file is
/// transcribed. Mirrors <see cref="LazyOcrEngine"/>'s deferred-init pattern;
/// differs only where the <see cref="IAudioTranscriber"/> contract requires
/// <see cref="IAsyncDisposable"/> and async-streaming semantics.
/// </summary>
/// <remarks>
/// The model size is decided once per process (either explicitly via the
/// constructor parameter or via <see cref="WhisperEnvironment.RecommendModelSize"/>).
/// Callers' <see cref="AudioExtractionOptions.ModelSize"/> is intentionally
/// overridden inside <see cref="TranscribeAsync"/> so a single indexing run
/// produces a consistent corpus — see the work-order rationale (option A) at
/// <c>todo/89-1. mcp-rag-audio-integration-v0.1.md</c>.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class LazyAudioTranscriber : IAudioTranscriber
{
    private readonly WhisperModelSize _modelSize;
    private readonly Lazy<Task<WhisperTranscriber>> _inner;

    /// <summary>
    /// The most recently constructed instance. Exposed so that the indexing
    /// pipeline can stamp transcript chunks with the resolved model size
    /// without having to thread a transcriber reference through call sites
    /// that don't otherwise depend on <c>FieldCure.DocumentParsers.Audio</c>.
    /// Single-instance assumption matches the server's startup wiring.
    /// </summary>
    internal static LazyAudioTranscriber? Current { get; private set; }

    /// <summary>The Whisper model size this transcriber is bound to.</summary>
    public WhisperModelSize ModelSize => _modelSize;

    /// <summary>
    /// Creates a lazy transcriber that initializes Whisper on first use.
    /// </summary>
    /// <param name="modelSize">
    /// Optional explicit model size. When <see langword="null"/> (default),
    /// the size is determined by <see cref="WhisperEnvironment.RecommendModelSize"/>
    /// using <see cref="QualityBias.Accuracy"/>, which is appropriate for the
    /// batch indexing scenario this transcriber is built for.
    /// </param>
    public LazyAudioTranscriber(WhisperModelSize? modelSize = null)
    {
        _modelSize = modelSize ?? WhisperEnvironment.RecommendModelSize();
        _inner = new Lazy<Task<WhisperTranscriber>>(
            InitializeAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        Current = this;
    }

    /// <summary>
    /// Constructs the inner <see cref="WhisperTranscriber"/>. The actual ggml
    /// download and native-runtime load happens lazily inside Whisper.net on
    /// the first <c>ProcessAsync</c> call, not here — eager warm-up would
    /// block server startup even when no audio file is ever indexed.
    /// </summary>
    private Task<WhisperTranscriber> InitializeAsync()
    {
        Console.Error.WriteLine(
            $"[Audio] Initializing Whisper transcriber with model size {_modelSize}.");
        try
        {
            return Task.FromResult(new WhisperTranscriber());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[Audio] Whisper transcriber initialization failed: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        Stream pcmStream,
        AudioExtractionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inner = await _inner.Value.ConfigureAwait(false);

        // Library-side policy override: ignore the caller's ModelSize so that a
        // single indexing run produces a consistent corpus. Callers needing a
        // different size construct a fresh LazyAudioTranscriber(WhisperModelSize.X).
        var effective = options.WithModelSize(_modelSize);

        await foreach (var segment in inner.TranscribeAsync(pcmStream, effective, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return segment;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_inner.IsValueCreated)
        {
            try
            {
                var inner = await _inner.Value.ConfigureAwait(false);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Initialization failure was already logged in InitializeAsync;
                // there is nothing left to dispose.
            }
        }
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
