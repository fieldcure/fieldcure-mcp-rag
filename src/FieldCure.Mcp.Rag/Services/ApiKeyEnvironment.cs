namespace FieldCure.Mcp.Rag.Services;

/// <summary>
/// Maps provider presets to canonical environment variable names and reads them
/// without any interactive fallback. Used by batch indexing paths.
/// </summary>
public static class ApiKeyEnvironment
{
    /// <summary>
    /// Maps a provider preset name to its canonical API key environment variable.
    /// </summary>
    /// <param name="presetName">Provider preset such as <c>openai</c> or <c>claude</c>.</param>
    /// <returns>The canonical environment variable name, or <see langword="null"/> when the preset is blank.</returns>
    public static string? GetEnvVarName(string? presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return null;

        return presetName.ToUpperInvariant() switch
        {
            "OPENAI" => "OPENAI_API_KEY",
            "CLAUDE" or "ANTHROPIC" => "ANTHROPIC_API_KEY",
            "GEMINI" or "GOOGLE" => "GEMINI_API_KEY",
            "VOYAGE" => "VOYAGE_API_KEY",
            "GROQ" => "GROQ_API_KEY",
            _ => $"{presetName.ToUpperInvariant()}_API_KEY",
        };
    }

    /// <summary>
    /// Resolves a provider preset's API key from the environment without any
    /// interactive fallback, returning an empty string when unset.
    /// </summary>
    /// <param name="presetName">Provider preset such as <c>openai</c> or <c>claude</c>.</param>
    /// <returns>The environment value, or an empty string when unavailable.</returns>
    public static string ResolveOrEmpty(string? presetName)
    {
        var envVarName = GetEnvVarName(presetName);
        return envVarName is null ? "" : Environment.GetEnvironmentVariable(envVarName) ?? "";
    }
}
