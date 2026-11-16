using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace FrameLine.Core.Mcp;

/// Bearer token generation and constant-time checking, plus the DNS-rebinding guards.
public static class McpAuth
{
    /// 32 random bytes (256 bits) from the system CSPRNG, lowercase hex (64 chars).
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// Compares every byte whatever the first mismatch, and the lengths without an early exit.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static bool ConstantTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        int diff = (x.Length ^ y.Length) | (x.Length == y.Length ? 0 : 1);
        int n = Math.Max(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            int p = i < x.Length ? x[i] : 0;
            int q = i < y.Length ? y[i] : 0;
            diff |= p ^ q;
        }
        return diff == 0;
    }

    /// "Authorization: Bearer token" (scheme case-insensitive) against the expected token.
    public static bool IsAuthorized(string? header, string token)
    {
        if (header is null || token.Length == 0) return false;
        var trimmed = header.TrimStart(' ');
        int space = trimmed.IndexOf(' ');
        if (space <= 0) return false;
        var scheme = trimmed[..space];
        var value = trimmed[(space + 1)..].Trim(' ', '\t');
        if (!scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase)) return false;
        return ConstantTimeEquals(value, token);
    }

    /// DNS-rebinding guard: only loopback names, whatever the port.
    public static bool IsAllowedHost(string? host)
    {
        var h = host?.Trim(' ', '\t').ToLowerInvariant();
        if (string.IsNullOrEmpty(h)) return false;
        int colon = h.LastIndexOf(':');
        if (colon >= 0)
        {
            var port = h[(colon + 1)..];
            if (port.Length == 0 || !port.All(char.IsAsciiDigit)) return false;
            h = h[..colon];
        }
        return h is "127.0.0.1" or "localhost";
    }

    /// Browsers send Origin on cross-site requests: only a loopback origin is accepted.
    public static bool IsAllowedOrigin(string? origin)
    {
        if (origin is null) return true;                             // non-browser clients send none
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https")) return false;
        var host = uri.Host.ToLowerInvariant();
        return host is "127.0.0.1" or "localhost";
    }
}
