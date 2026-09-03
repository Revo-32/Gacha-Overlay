using System.Security.Cryptography;

namespace LSOverlay.Backend.Security;

internal static class CryptographicSecrets
{
    public static string CreateClaimSecret() => CreateOpaqueSecret();

    public static string CreateAccessToken() => $"lso_{CreateOpaqueSecret()}";

    public static byte[] Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    public static string HashHex(string secret) => Convert.ToHexString(Hash(secret));

    public static bool FixedTimeEquals(string secret, byte[] expectedHash)
    {
        var actual = Hash(secret);
        return expectedHash.Length == actual.Length &&
            CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static string CreateOpaqueSecret()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        return Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
