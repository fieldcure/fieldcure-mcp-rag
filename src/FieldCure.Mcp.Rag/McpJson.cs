using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldCure.Mcp.Rag;

/// <summary>
/// Shared JSON serialization options for MCP tool responses and configuration.
/// Uses relaxed encoding so non-ASCII characters (Korean, CJK, emoji, etc.)
/// are emitted as-is instead of \uXXXX escape sequences.
/// </summary>
internal static class McpJson
{
    /// <summary>Search tool options: indented, enum as snake_case string.</summary>
    public static readonly JsonSerializerOptions Search = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Simple indented options for tool responses.</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Configuration store options: camelCase, skip nulls.</summary>
    public static readonly JsonSerializerOptions Config = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
