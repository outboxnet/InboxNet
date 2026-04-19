using System.Security.Cryptography;
using System.Text;

namespace InboxNet.Inbox.Providers;

internal static class WebhookSignatureHelpers
{
    /// <summary>
    /// Computes lowercase hex SHA-256 of a UTF-8 string.
    /// </summary>
    public static string ComputeContentSha256(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes HMAC-SHA256 of <paramref name="message"/> using <paramref name="key"/>.
    /// Returns the raw signature bytes for constant-time comparison.
    /// </summary>
    public static byte[] ComputeHmacSha256(byte[] key, string message)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
    }

    /// <summary>
    /// Constant-time comparison of two hex-encoded signatures. Case-insensitive.
    /// Returns false if the inputs have different lengths.
    /// </summary>
    public static bool FixedTimeHexEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var ca = a[i];
            var cb = b[i];
            if (ca >= 'A' && ca <= 'Z') ca = (char)(ca + 32);
            if (cb >= 'A' && cb <= 'Z') cb = (char)(cb + 32);
            diff |= ca ^ cb;
        }
        return diff == 0;
    }

    /// <summary>
    /// Constant-time comparison between a hex-encoded signature and raw bytes.
    /// </summary>
    public static bool FixedTimeHexEquals(string hex, byte[] raw)
    {
        if (hex.Length != raw.Length * 2) return false;
        var expected = Convert.ToHexString(raw).ToLowerInvariant();
        return FixedTimeHexEquals(hex, expected);
    }
}
