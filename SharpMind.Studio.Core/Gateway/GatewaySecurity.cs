using System.Security.Cryptography;
using System.Text;

namespace SharpMind.Studio.Core.Gateway;

public static class GatewaySecurity
{
    public static string NewToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static string HashPin(string pin, string salt)
    {
        byte[] saltBytes = Convert.FromBase64String(salt);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            pin, saltBytes, 120_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(hash);
    }

    public static bool VerifyPin(string pin, string salt, string expectedHash)
    {
        try
        {
            byte[] actual = Convert.FromBase64String(HashPin(pin, salt));
            byte[] expected = Convert.FromBase64String(expectedHash);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
