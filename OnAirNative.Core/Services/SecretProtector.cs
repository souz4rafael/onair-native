using System.Security.Cryptography;
using System.Text;

namespace OnAirNative.Services;

/// <summary>
/// Encrypts secrets at rest with Windows DPAPI (per-user scope). Stored values
/// are base64 with a version prefix; plaintext (un-prefixed) values pass through
/// unchanged so configs written by older builds migrate on the next save.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:v1:";

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.StartsWith(Prefix, StringComparison.Ordinal))
            return plain ?? "";
        try
        {
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(enc);
        }
        catch
        {
            return plain; // fallback: leave as-is if DPAPI is unavailable
        }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored ?? "";
        try
        {
            var raw = Convert.FromBase64String(stored[Prefix.Length..]);
            var dec = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch
        {
            return "";
        }
    }
}
