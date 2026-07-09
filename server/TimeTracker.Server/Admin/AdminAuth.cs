using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using TimeTracker.Server.Options;

namespace TimeTracker.Server.Admin;

public static class AdminAuth
{
    public static bool TryAuthenticate(HttpRequest request, TimeTrackerOptions options)
    {
        if (!request.Headers.TryGetValue("Authorization", out StringValues headerValue)) return false;

        var header = headerValue.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;

        string decoded;
        try
        {
            var encoded = header["Basic ".Length..].Trim();
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return false;
        }

        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex < 0) return false;

        var username = decoded[..separatorIndex];
        var password = decoded[(separatorIndex + 1)..];

        return FixedTimeEquals(username, options.AdminUsername) && FixedTimeEquals(password, options.AdminPassword);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length) return false;

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
