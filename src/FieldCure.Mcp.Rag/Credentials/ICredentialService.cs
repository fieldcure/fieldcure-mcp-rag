namespace FieldCure.Mcp.Rag.Credentials;

/// <summary>
/// Credential resolution service for API keys.
/// Reads from Windows Credential Manager (PasswordVault) shared with AssistStudio.
/// </summary>
public interface ICredentialService
{
    /// <summary>Retrieves the API key for a provider preset.</summary>
    string? GetApiKey(string presetName);
}
