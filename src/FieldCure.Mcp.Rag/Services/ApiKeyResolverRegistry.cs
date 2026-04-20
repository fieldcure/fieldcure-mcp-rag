using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FieldCure.Mcp.Rag.Services;

/// <summary>
/// Caches one API key resolver per environment variable so interactive provider
/// selection can lazily elicit keys on first use and invalidate them on auth failures.
/// </summary>
public sealed class ApiKeyResolverRegistry
{
    readonly ConcurrentDictionary<string, ApiKeyResolver> _resolvers = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves an API key from environment variables first and then, when supported,
    /// through MCP elicitation for the active request.
    /// </summary>
    /// <param name="server">The active MCP server instance.</param>
    /// <param name="envVarName">The canonical provider API key environment variable.</param>
    /// <param name="providerLabel">User-facing provider name shown in the elicitation prompt.</param>
    /// <param name="ct">Cancellation token for the resolution flow.</param>
    /// <returns>The resolved API key, or <see langword="null"/> when none is available.</returns>
    public Task<string?> ResolveAsync(McpServer server, string envVarName, string providerLabel, CancellationToken ct) =>
        _resolvers.GetOrAdd(envVarName, static key => new ApiKeyResolver(key)).ResolveAsync(server, providerLabel, ct);

    /// <summary>
    /// Invalidates a cached key for the given environment variable so the next
    /// request can re-prompt the user instead of reusing the stale value.
    /// </summary>
    /// <param name="envVarName">The canonical provider API key environment variable.</param>
    public void Invalidate(string envVarName)
    {
        if (_resolvers.TryGetValue(envVarName, out var resolver))
            resolver.Invalidate();
    }

    /// <summary>
    /// Builds a user-facing soft-fail message describing how to configure a missing API key.
    /// </summary>
    /// <param name="envVarName">The environment variable that can satisfy the key request.</param>
    /// <returns>A concise configuration hint for tool responses.</returns>
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

    /// <summary>
    /// Resolves and caches one concrete API key for a single provider environment variable.
    /// </summary>
    /// <param name="server">The active MCP server instance.</param>
    /// <param name="providerLabel">User-facing provider name shown in the elicitation prompt.</param>
    /// <param name="ct">Cancellation token for the resolution flow.</param>
    /// <returns>The resolved API key, or <see langword="null"/> when no key is available.</returns>
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

    /// <summary>
    /// Discards the cached key and marks static sources as exhausted so the
    /// next resolution will prefer elicitation over reusing the same env value.
    /// </summary>
    public void Invalidate()
    {
        _cachedKey = null;
        _staticSourcesExhausted = true;
    }
}
