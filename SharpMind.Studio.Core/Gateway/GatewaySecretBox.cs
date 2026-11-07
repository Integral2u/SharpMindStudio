using System.Security.Cryptography;
using System.Text;

namespace SharpMind.Studio.Core.Gateway;

/// <summary>
/// Encrypts gateway secrets at rest with AES-GCM. The key is generated once
/// next to the gateway data and never leaves the user's profile. On Windows the
/// key file itself is wrapped with DPAPI (current user), so copying the profile
/// to another account or machine does not expose the stored secrets; restore a
/// backup as the same user on the same machine. Other platforms keep the raw key
/// file with owner-only permissions, which is what the Docker image uses.
/// </summary>
public static class GatewaySecretBox
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const byte Version = 1;
    private const string DpapiSuffix = ".dpapi";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SharpMind.Studio.SecretKey.v1");
    private static readonly object KeyGate = new();

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
        if (!HasKey(keyFile)) return null;

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
            byte[]? key = TryLoadKey(keyFile);
            if (key is null) return null;

            ReadOnlySpan<byte> nonce = packed.AsSpan(1, NonceSize);
            ReadOnlySpan<byte> tag = packed.AsSpan(1 + NonceSize, TagSize);
            ReadOnlySpan<byte> cipher = packed.AsSpan(1 + NonceSize + TagSize);
            byte[] plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException
            or UnauthorizedAccessException or PlatformNotSupportedException or InvalidDataException)
        {
            return null;
        }
    }

    public static byte[] EnsureKey(string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        string full = Path.GetFullPath(keyFile);

        lock (KeyGate)
        {
            byte[]? existing = TryLoadKeyCore(full);
            if (existing is not null) return existing;

            return SaveKeyCore(full, RandomNumberGenerator.GetBytes(KeySize));
        }
    }

    private static bool HasKey(string keyFile) =>
        File.Exists(keyFile) || File.Exists(keyFile + DpapiSuffix);

    private static byte[]? TryLoadKey(string keyFile)
    {
        lock (KeyGate)
        {
            return TryLoadKeyCore(Path.GetFullPath(keyFile));
        }
    }

    private static byte[]? TryLoadKeyCore(string full)
    {
        string dpapi = full + DpapiSuffix;
        if (File.Exists(dpapi))
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidDataException(
                    $"'{dpapi}' is DPAPI-protected and can only be read on Windows.");

            byte[] key = ProtectedData.Unprotect(
                File.ReadAllBytes(dpapi),
                Entropy,
                DataProtectionScope.CurrentUser);
            ValidateKey(key, dpapi);
            return key;
        }

        if (!File.Exists(full)) return null;

        byte[] legacy = File.ReadAllBytes(full);
        ValidateKey(legacy, full);

        if (OperatingSystem.IsWindows())
        {
            // Migrate a pre-DPAPI key in place: keep only the protected copy.
            byte[] effective = SaveKeyCore(full, legacy);
            TryDelete(full);
            return effective;
        }

        return legacy;
    }

    /// <summary>
    /// Writes the key, returning the key that actually won a concurrent create.
    /// On Windows only the DPAPI blob is written.
    /// </summary>
    private static byte[] SaveKeyCore(string full, byte[] key)
    {
        string? folder = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

        if (OperatingSystem.IsWindows())
        {
            string dpapi = full + DpapiSuffix;
            byte[] blob = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                using var stream = new FileStream(
                    dpapi, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(blob);
                return key;
            }
            catch (IOException) when (File.Exists(dpapi))
            {
                byte[] existing = ProtectedData.Unprotect(
                    File.ReadAllBytes(dpapi),
                    Entropy,
                    DataProtectionScope.CurrentUser);
                ValidateKey(existing, dpapi);
                return existing;
            }
        }

        try
        {
            using var stream = new FileStream(
                full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(key);
            SetOwnerOnly(full);
            return key;
        }
        catch (IOException) when (File.Exists(full))
        {
            byte[] existing = File.ReadAllBytes(full);
            ValidateKey(existing, full);
            return existing;
        }
    }

    private static void ValidateKey(byte[] key, string path)
    {
        if (key.Length != KeySize)
            throw new InvalidDataException($"Secret key '{path}' is not {KeySize} bytes.");
    }

    private static void SetOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            // A filesystem without Unix permissions: the key still works.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover legacy key is superseded by the DPAPI copy.
        }
    }
}
