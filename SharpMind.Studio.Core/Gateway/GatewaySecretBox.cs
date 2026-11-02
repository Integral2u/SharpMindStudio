using System.Security.Cryptography;
using System.Text;

namespace SharpMind.Studio.Core.Gateway;

/// <summary>
/// Encrypts gateway secrets at rest with AES-GCM. The key is generated once
/// next to the gateway data and never leaves the user's profile. Without the
/// key file the stored values cannot be recovered, so a copy of that file is
/// part of backing up the gateway.
/// </summary>
public static class GatewaySecretBox
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const byte Version = 1;

    public static string Protect(string plaintext, string keyFile)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        byte[] key = EnsureKey(keyFile);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        byte[] packed = new byte[1 + NonceSize + TagSize + cipher.Length];
        packed[0] = Version;
        nonce.CopyTo(packed, 1);
        tag.CopyTo(packed, 1 + NonceSize);
        cipher.CopyTo(packed, 1 + NonceSize + TagSize);
        return Convert.ToBase64String(packed);
    }

    public static string? Unprotect(string? protectedText, string keyFile)
    {
        if (string.IsNullOrWhiteSpace(protectedText)) return null;
        if (!File.Exists(keyFile)) return null;

        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(protectedText);
        }
        catch (FormatException)
        {
            return null;
        }

        if (packed.Length <= 1 + NonceSize + TagSize || packed[0] != Version) return null;

        try
        {
            byte[] key = File.ReadAllBytes(keyFile);
            if (key.Length != KeySize) return null;

            ReadOnlySpan<byte> nonce = packed.AsSpan(1, NonceSize);
            ReadOnlySpan<byte> tag = packed.AsSpan(1 + NonceSize, TagSize);
            ReadOnlySpan<byte> cipher = packed.AsSpan(1 + NonceSize + TagSize);
            byte[] plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static byte[] EnsureKey(string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        if (File.Exists(keyFile))
        {
            byte[] existing = File.ReadAllBytes(keyFile);
            if (existing.Length == KeySize) return existing;
            throw new InvalidDataException($"Gateway key '{keyFile}' is not {KeySize} bytes.");
        }

        string? folder = Path.GetDirectoryName(Path.GetFullPath(keyFile));
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            using var stream = new FileStream(keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(key);
        }
        catch (IOException) when (File.Exists(keyFile))
        {
            // Another process created the key first; use that one.
            byte[] existing = File.ReadAllBytes(keyFile);
            if (existing.Length != KeySize)
                throw new InvalidDataException($"Gateway key '{keyFile}' is not {KeySize} bytes.");
            return existing;
        }

        return key;
    }
}
