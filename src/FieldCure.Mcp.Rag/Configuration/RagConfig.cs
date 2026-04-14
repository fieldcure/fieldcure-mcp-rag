using System.Text.Json;

namespace FieldCure.Mcp.Rag.Configuration;

/// <summary>
/// Knowledge base configuration stored in {kb-path}/config.json.
/// Created by AssistStudio when the user adds a new knowledge base.
/// </summary>
public sealed class RagConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Created { get; set; } = "";
    public List<string> SourcePaths { get; set; } = [];

    public ProviderConfig Contextualizer { get; set; } = new();
    public ProviderConfig Embedding { get; set; } = new();

    /// <summary>Custom system prompt for chunk contextualization (null = built-in default).</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Loads config.json from a knowledge base folder.</summary>
    public static RagConfig Load(string kbPath)
    {
        var configPath = Path.Combine(kbPath, "config.json");
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"config.json not found in {kbPath}");

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<RagConfig>(json, McpJson.Config)
               ?? throw new InvalidOperationException("Failed to deserialize config.json");
    }

    /// <summary>Saves config.json to a knowledge base folder.</summary>
    public void Save(string kbPath)
    {
        var configPath = Path.Combine(kbPath, "config.json");
        var json = JsonSerializer.Serialize(this, McpJson.Config);
        File.WriteAllText(configPath, json);
    }
}

/// <summary>
/// Provider configuration for contextualizer or embedding.
/// </summary>
public sealed class ProviderConfig
{
    /// <summary>Provider type: "openai", "anthropic", "ollama", etc.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Model ID (e.g., "claude-sonnet-4-20250514", "text-embedding-3-small").</summary>
    public string Model { get; set; } = "";

    /// <summary>API base URL. Null = provider default.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// PasswordVault preset name for API key lookup (e.g., "Claude", "OpenAI").
    /// Null for providers that don't require a key (e.g., local Ollama).
    /// </summary>
    public string? ApiKeyPreset { get; set; }

    /// <summary>Vector dimension override (embedding only, 0 = auto-detect).</summary>
    public int Dimension { get; set; }
}
