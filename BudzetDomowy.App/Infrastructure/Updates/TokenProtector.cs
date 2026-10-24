using System.Security.Cryptography;
using System.Text;

namespace BudzetDomowy.App.Infrastructure.Updates;

/// <summary>Keeps the GitHub token in the settings file readable only by the current Windows user (DPAPI).</summary>
public static class TokenProtector
{
    public static string? Protect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;   // written by another Windows user or damaged: behave as if there was no token
        }
    }
}
