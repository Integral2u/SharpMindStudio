using SharpMind.Studio.Core.Gateway;

namespace SharpMind.Studio.Core.Vault;

/// <summary>
/// A named secret. The value is AES-GCM ciphertext bound to a key file next to
/// the vault, using the same box as the Gateway and provider keys. The name is
/// the identity: references are case-insensitive, but the stored spelling is
/// what the UI shows.
/// </summary>
public sealed record VaultSecret
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ValueProtected { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed record VaultDocument
{
    public const string CurrentSchema = "studio-vault/1";

    public string Schema { get; init; } = CurrentSchema;
    public List<VaultSecret> Secrets { get; init; } = [];
}

/// <summary>The public view of a secret: everything except the ciphertext.</summary>
public sealed record VaultSecretInfo(
    string Name,
    string Description,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public enum VaultSecretState
{
    Missing,
    Ok,
    Unreadable,
}

/// <summary>A secret name, resolved to its value only at the moment it is used.</summary>
public interface ISecretResolver
{
    string? Resolve(string name);
}

/// <summary>Used by tests and by callers that only accept inline secrets.</summary>
public sealed class NullSecretResolver : ISecretResolver
{
    public static readonly NullSecretResolver Instance = new();

    private NullSecretResolver()
    {
    }

    public string? Resolve(string name) => null;
}

/// <summary>
/// One or more <c>%vault:NAME%</c> references did not resolve. Callers show
/// the message to the user (or the model) and refuse to start the work.
/// </summary>
public sealed class SecretMissingException : InvalidOperationException
{
    public SecretMissingException(IReadOnlyList<string> names)
        : base(BuildMessage(names))
    {
        Names = names;
    }

    public IReadOnlyList<string> Names { get; }

    private static string BuildMessage(IReadOnlyList<string> names)
    {
        string list = string.Join(", ", names);
        return names.Count == 1
            ? $"Secret '{list}' is not in the Vault. Add it in Settings > Vault."
            : $"Secrets '{list}' are not in the Vault. Add them in Settings > Vault.";
    }
}

/// <summary>JSON-backed named secrets. Plaintext is never written or cached.</summary>
public sealed class VaultStore : ISecretResolver
{
    public const string FileName = "vault.json";
    public const int MaxNameLength = 64;
    public const int MinValueLength = 4;
    public const int MaxValueLength = 16_384;
    public const int MaxDescriptionLength = 512;

    private readonly Lock _gate = new();
    private readonly string _keyFile;
    private VaultDocument _document;

    public VaultStore(string path, string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        Path = System.IO.Path.GetFullPath(path);
        _keyFile = System.IO.Path.GetFullPath(keyFile);

        string? folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
        _document = Load();
    }

    public string Path { get; }

    public IReadOnlyList<VaultSecretInfo> Secrets
    {
        get
        {
            lock (_gate)
            {
                return [.. _document.Secrets
                    .OrderBy(secret => secret.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(Info)];
            }
        }
    }

    public VaultSecretInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_gate)
        {
            VaultSecret? secret = FindLocked(name);
            return secret is null ? null : Info(secret);
        }
    }

    public bool Contains(string? name) => Find(name) is not null;

    /// <summary>
    /// The value, or null when the name is missing or the key cannot be read on
    /// this account. <see cref="State"/> tells the two apart for the UI.
    /// </summary>
    public string? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_gate)
        {
            VaultSecret? secret = FindLocked(name);
            if (secret is null) return null;
            return GatewaySecretBox.Unprotect(secret.ValueProtected, _keyFile);
        }
    }

    public VaultSecretState State(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return VaultSecretState.Missing;
        lock (_gate)
        {
            VaultSecret? secret = FindLocked(name);
            if (secret is null) return VaultSecretState.Missing;

            return GatewaySecretBox.Unprotect(secret.ValueProtected, _keyFile) is null
                ? VaultSecretState.Unreadable
                : VaultSecretState.Ok;
        }
    }

    /// <summary>Creates or replaces a secret. The name is immutable once stored.</summary>
    public VaultSecretInfo Set(string name, string value, string? description = null)
    {
        string cleanName = ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A secret needs a value.", nameof(value));
        if (value.Length < MinValueLength)
            throw new ArgumentException(
                $"A secret must be at least {MinValueLength} characters, so redaction can remove it safely.",
                nameof(value));
        if (value.Length > MaxValueLength)
            throw new ArgumentException(
                $"A secret can be at most {MaxValueLength:N0} characters.", nameof(value));

        string cleanDescription = (description ?? "").Trim();
        if (cleanDescription.Length > MaxDescriptionLength)
            throw new ArgumentException(
                $"A description can be at most {MaxDescriptionLength:N0} characters.",
                nameof(description));

        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string protectedValue = GatewaySecretBox.Protect(value, _keyFile);
            int index = _document.Secrets.FindIndex(secret =>
                secret.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase));

            VaultSecret stored;
            if (index >= 0)
            {
                VaultSecret existing = _document.Secrets[index];
                stored = existing with
                {
                    ValueProtected = protectedValue,
                    Description = cleanDescription,
                    UpdatedUtc = now,
                };
                _document.Secrets[index] = stored;
            }
            else
            {
                stored = new VaultSecret
                {
                    Name = cleanName,
                    Description = cleanDescription,
                    ValueProtected = protectedValue,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };
                _document.Secrets.Add(stored);
            }

            SaveLocked();
            return Info(stored);
        }
    }

    public bool Remove(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        lock (_gate)
        {
            int removed = _document.Secrets.RemoveAll(secret =>
                secret.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;

            SaveLocked();
            return true;
        }
    }

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= MaxNameLength &&
        NameRules.IsMatch(name);

    public static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string clean = name.Trim();
        if (clean.Length > MaxNameLength)
            throw new ArgumentException(
                $"A secret name can be at most {MaxNameLength} characters.", nameof(name));
        if (!NameRules.IsMatch(clean))
            throw new ArgumentException(
                "A secret name must start with a letter or underscore and contain only letters, digits and underscores.",
                nameof(name));
        return clean;
    }

    private VaultSecret? FindLocked(string name)
    {
        string clean = name.Trim();
        return _document.Secrets.FirstOrDefault(secret =>
            secret.Name.Equals(clean, StringComparison.OrdinalIgnoreCase));
    }

    private static VaultSecretInfo Info(VaultSecret secret) =>
        new(secret.Name, secret.Description, secret.CreatedUtc, secret.UpdatedUtc);

    private VaultDocument Load()
    {
        try
        {
            if (!File.Exists(Path)) return new VaultDocument();

            VaultDocument document = Serialization.JsonFile.Read<VaultDocument>(Path);
            if (document.Schema != VaultDocument.CurrentSchema)
                return new VaultDocument();

            // Names are case-insensitive. A hand-edited file could contain
            // duplicates; keep the last one so the newest value wins.
            var seen = new Dictionary<string, VaultSecret>(StringComparer.OrdinalIgnoreCase);
            foreach (VaultSecret secret in document.Secrets ?? [])
            {
                if (string.IsNullOrWhiteSpace(secret.Name)) continue;
                string name = secret.Name.Trim();
                if (!IsValidName(name)) continue;

                seen[name] = secret with
                {
                    Name = name,
                    Description = (secret.Description ?? "").Trim(),
                };
            }

            return new VaultDocument { Secrets = [.. seen.Values] };
        }
        catch
        {
            // A damaged file must not stop Studio from starting. The user sees
            // an empty vault and can replace the secrets.
            return new VaultDocument();
        }
    }

    private void SaveLocked() => Serialization.JsonFile.Write(Path, _document);

    private static readonly System.Text.RegularExpressions.Regex NameRules = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
