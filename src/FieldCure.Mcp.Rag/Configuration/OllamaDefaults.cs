namespace FieldCure.Mcp.Rag.Configuration;

/// <summary>
/// Default values for Ollama-specific parameters shared across
/// embedding and contextualization providers.
/// </summary>
internal static class OllamaDefaults
{
    /// <summary>
    /// Duration to keep the model loaded in VRAM after the last request.
    /// Matches Ollama's built-in default (5 minutes). Users can override
    /// per-KB via config.json: "0" for immediate unload, "-1" for permanent,
    /// or a Go duration like "30m".
    /// </summary>
    public const string KeepAlive = "5m";

    /// <summary>
    /// Context window size in tokens. Ollama's built-in default of 2048
    /// is too small for meaningful chunk contextualization.
    /// Applies to contextualizer only; embedding models ignore this.
    /// </summary>
    public const int NumCtx = 8192;
}
