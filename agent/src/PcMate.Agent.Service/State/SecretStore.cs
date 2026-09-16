using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PcMate.Core.Crypto;

namespace PcMate.Agent.Service.State;

/// <summary>
/// Хранение секретов агента через DPAPI (область LocalMachine, дополнительная энтропия).
/// Ключ нельзя вынести на другой компьютер вместе с файлом.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PcMate/v1/agent-identity");

    public static string Protect(byte[] secret)
    {
        var blob = ProtectedData.Protect(secret, Entropy, DataProtectionScope.LocalMachine);
        return B64Url.Encode(blob);
    }

    public static byte[] Unprotect(string protectedB64)
    {
        var blob = B64Url.Decode(protectedB64);
        return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
    }

    public static bool TryUnprotect(string? protectedB64, out byte[] secret)
    {
        secret = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(protectedB64)) return false;
        try
        {
            secret = Unprotect(protectedB64!);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
