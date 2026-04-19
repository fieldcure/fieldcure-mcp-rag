namespace FieldCure.Mcp.Rag.Services;

/// <summary>
/// Maps provider presets to canonical environment variable names and reads them
/// without any interactive fallback. Used by batch indexing paths.
/// </summary>
public static class ApiKeyEnvironment
{
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

    public static string ResolveOrEmpty(string? presetName)
    {
        var envVarName = GetEnvVarName(presetName);
        return envVarName is null ? "" : Environment.GetEnvironmentVariable(envVarName) ?? "";
    }
}
