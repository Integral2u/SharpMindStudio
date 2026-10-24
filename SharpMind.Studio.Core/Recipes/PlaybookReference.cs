namespace SharpMind.Studio.Core.Recipes;

/// <summary>A versioned partial Recipe plus the fields this Recipe overrides.</summary>
public sealed record PlaybookReference
{
    public string Id { get; init; } = "";
    public int Version { get; init; } = 1;
    public List<string> Overrides { get; init; } = [];
}
