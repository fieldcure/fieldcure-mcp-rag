using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FieldCure.Mcp.Rag.Credentials;

/// <summary>
/// Windows Credential Manager based credential service.
/// Reads credentials stored by AssistStudio's UWP PasswordVault
/// (Resource = "FieldCure.AssistStudio", UserName = provider preset name).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialService : ICredentialService
{
    const string Resource = "FieldCure.AssistStudio";
    const int CredTypeGeneric = 1;

    public string? GetApiKey(string presetName) =>
        RetrieveByUserName(presetName);

    /// <summary>
    /// Retrieves a credential by matching UserName across all entries under the resource.
    /// </summary>
    string? RetrieveByUserName(string userName)
    {
        if (!CredEnumerate($"{Resource}*", 0, out var count, out var credArrayPtr))
            return null;

        try
        {
            for (int i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(credArrayPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);

                if (cred.Type == CredTypeGeneric
                    && string.Equals(cred.UserName, userName, StringComparison.Ordinal)
                    && cred.CredentialBlobSize > 0
                    && cred.CredentialBlob != IntPtr.Zero)
                {
                    var bytes = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                    return Encoding.Unicode.GetString(bytes);
                }
            }
        }
        finally
        {
            CredFree(credArrayPtr);
        }

        return null;
    }

    #region P/Invoke

#pragma warning disable SYSLIB1054
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern void CredFree(IntPtr buffer);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    #endregion
}
