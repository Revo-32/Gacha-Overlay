using System.Security.Cryptography;

namespace LSOverlay.Backend.Security;

internal static class CryptographicSecrets
{
    private const string UserCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string CreateUserCode()
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        Span<char> code = stackalloc char[9];
        for (var index = 0; index < random.Length; index++)
        {
            code[index + (index >= 4 ? 1 : 0)] =
                UserCodeAlphabet[random[index] & 31];
        }

        code[4] = '-';
        return new string(code);
    }

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

    public static string NormalizeUserCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new string(code
            .Where(character => character != '-' && !char.IsWhiteSpace(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
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
