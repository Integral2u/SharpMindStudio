using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Tests;

/// <summary>A resolver backed by a dictionary, for tests that do not need a file.</summary>
internal sealed class TestSecretResolver : ISecretResolver
{
    private readonly Dictionary<string, string> _values =
        new(StringComparer.OrdinalIgnoreCase);

    public TestSecretResolver(params (string Name, string Value)[] values)
    {
        foreach ((string name, string value) in values) _values[name] = value;
    }

    public TestSecretResolver Add(string name, string value)
    {
        _values[name] = value;
        return this;
    }

    public string? Resolve(string name) =>
        _values.TryGetValue(name, out string? value) ? value : null;
}
