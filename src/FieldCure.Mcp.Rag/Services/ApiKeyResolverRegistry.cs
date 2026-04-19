using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Services;

public sealed class ApiKeyResolverRegistry
{
    readonly ConcurrentDictionary<string, ApiKeyResolver> _resolvers = new(StringComparer.Ordinal);

    public Task<string?> ResolveAsync(McpServer server, string envVarName, string providerLabel, CancellationToken ct) =>
        _resolvers.GetOrAdd(envVarName, static key => new ApiKeyResolver(key)).ResolveAsync(server, providerLabel, ct);

    public void Invalidate(string envVarName)
    {
        if (_resolvers.TryGetValue(envVarName, out var resolver))
            resolver.Invalidate();
    }

    public string BuildSoftFailMessage(string envVarName) =>
        $"API key not configured. Set {envVarName} environment variable, or use a client that supports MCP Elicitation.";
}

sealed class ApiKeyResolver(string envVarName)
{
    const int MaxReElicits = 2;

    readonly SemaphoreSlim _gate = new(1, 1);
    string? _cachedKey;
    bool _hasElicitedBefore;
    int _reElicitCount;
    bool _staticSourcesExhausted;

    public async Task<string?> ResolveAsync(McpServer server, string providerLabel, CancellationToken ct)
    {
        if (_cachedKey is not null)
            return _cachedKey;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cachedKey is not null)
                return _cachedKey;

            if (!_staticSourcesExhausted)
            {
                var envKey = Environment.GetEnvironmentVariable(envVarName);
                if (!string.IsNullOrWhiteSpace(envKey))
                {
                    _cachedKey = envKey;
                    return _cachedKey;
                }
            }

            if (server.ClientCapabilities?.Elicitation is null)
                return null;

            if (_hasElicitedBefore)
            {
                if (_reElicitCount >= MaxReElicits)
                    return null;
                _reElicitCount++;
            }

            _hasElicitedBefore = true;

            try
            {
                var result = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = $"Enter your {providerLabel} API key.",
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                        {
                            ["api_key"] = new ElicitRequestParams.StringSchema
                            {
                                Title = "API Key",
                                Description = $"{providerLabel} API Key",
                                MinLength = 1,
                            },
                        },
                        Required = ["api_key"],
                    },
                }, ct);

                if (!result.IsAccepted || result.Content is null || !result.Content.TryGetValue("api_key", out var value))
                    return null;

                var key = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(key))
                    return null;

                _cachedKey = key;
                return _cachedKey;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _cachedKey = null;
        _staticSourcesExhausted = true;
    }
}
