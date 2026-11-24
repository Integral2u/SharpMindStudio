namespace SharpMind.Studio.Core.Work;

/// <summary>How a command entry is gated when it runs.</summary>
public enum CommandPermission
{
    Ask,
    Allow,
    AllowAlways,
}

/// <summary>
/// One executable the softshell may run. The executable is resolved to a real
/// path before matching, so a different binary with the same name cannot stand
/// in for an allowed one.
/// </summary>
public sealed record AllowedCommand
{
    public string Name { get; init; } = "";
    public string Executable { get; init; } = "";
    public List<string> ArgumentsPrefix { get; init; } = [];
    public string Description { get; init; } = "";

    /// <summary>Ask each run, allow for this app session, or allow always.</summary>
    public CommandPermission Permission { get; init; } = CommandPermission.Ask;

    /// <summary>Display only. The Run permission is still the consent for what the child does.</summary>
    public bool UsesNetwork { get; init; }

    /// <summary>Display only. Curated wrappers use a real Delete gate where relevant.</summary>
    public bool Destructive { get; init; }

    /// <summary>Allow python/node and similar code hosts. Shells are never allowed.</summary>
    public bool AllowInterpreter { get; init; }
}

/// <summary>Starting points for the project's command line allowlist.</summary>
public static class AllowedCommandPresets
{
    public static IReadOnlyList<AllowedCommand> All { get; } =
    [
        new AllowedCommand
        {
            Name = "git",
            Executable = "git",
            Description = "Repository status, history and diffs.",
            UsesNetwork = true,
        },
        new AllowedCommand
        {
            Name = "dotnet",
            Executable = "dotnet",
            Description = ".NET build and test.",
            UsesNetwork = true,
        },
        new AllowedCommand
        {
            Name = "rg",
            Executable = "rg",
            Description = "Ripgrep text search.",
        },
        new AllowedCommand
        {
            Name = "curl",
            Executable = "curl",
            Description = "HTTP requests. Network access is visible in the permission prompt.",
            UsesNetwork = true,
        },
        new AllowedCommand
        {
            Name = "python",
            Executable = "python",
            Description = "Python scripts. This is an interpreter and can run arbitrary code.",
            AllowInterpreter = true,
        },
    ];

    public static AllowedCommand? Find(string? name) =>
        All.FirstOrDefault(preset =>
            preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
