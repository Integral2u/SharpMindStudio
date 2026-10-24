namespace SharpMind.Studio.Core.Projects;

/// <summary>The small identity file at the root of a Studio project.</summary>
public sealed record ProjectDocument
{
    public const string CurrentSchema = "studio-project/1";

    public string Schema { get; init; } = CurrentSchema;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? DeviceProfile { get; init; }
}
