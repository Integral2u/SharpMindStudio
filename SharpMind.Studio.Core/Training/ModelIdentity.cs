using System.Security.Cryptography;
using System.Text;
using SharpMind.Model;
using SharpMind.Model.Format;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// A cheap, stable identity for a model file, used to key permissions that only
/// make sense for that model's embedded tools. The embedded checksum is preferred
/// when the writer recorded one; otherwise the name, size and creation time are
/// enough to tell one model from another without hashing gigabytes.
/// </summary>
public sealed record ModelIdentity(
    string Id,
    string Name,
    long SizeBytes,
    DateTime CreatedUtc,
    string? Checksum)
{
    public static ModelIdentity None { get; } = new("", "", 0, default, null);

    public bool IsKnown => Id.Length > 0;

    public static ModelIdentity For(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return None;
        if (!path.EndsWith(".smm", StringComparison.OrdinalIgnoreCase)) return None;

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return None;

            string? checksum = ReadChecksum(path);
            string id = !string.IsNullOrWhiteSpace(checksum)
                ? "chk-" + ShortHash(checksum!)
                : "loc-" + ShortHash(
                    $"{file.Name}|{file.Length}|{file.CreationTimeUtc:O}");

            return new ModelIdentity(
                id,
                file.Name,
                file.Length,
                file.CreationTimeUtc,
                checksum);
        }
        catch
        {
            return None;
        }
    }

    private static string? ReadChecksum(string path)
    {
        try
        {
            ModelMetaData meta = SmmLoader.LoadMeta(path);
            string? value = meta.GetString("checksum");
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string ShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
