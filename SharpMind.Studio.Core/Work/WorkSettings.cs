using System.Text.Json.Serialization;

namespace SharpMind.Studio.Core.Work;

[JsonConverter(typeof(JsonStringEnumConverter<WorkPermission>))]
public enum WorkPermission
{
    Never,
    Ask,
    Always,
}

public enum WorkToolCategory
{
    Read,
    Write,
    Delete,
    Web,
    Execute,
}

public sealed record WorkPermissions
{
    public WorkPermission Read { get; init; } = WorkPermission.Ask;
    public WorkPermission Write { get; init; } = WorkPermission.Ask;
    public WorkPermission Delete { get; init; } = WorkPermission.Never;
    public WorkPermission Web { get; init; } = WorkPermission.Never;

    /// <summary>
    /// Running a program. Ask by default: the first command prompts, and the
    /// person can remember that choice for this folder.
    /// </summary>
    public WorkPermission Execute { get; init; } = WorkPermission.Ask;

    public WorkPermission For(WorkToolCategory category) => category switch
    {
        WorkToolCategory.Read => Read,
        WorkToolCategory.Write => Write,
        WorkToolCategory.Delete => Delete,
        WorkToolCategory.Web => Web,
        WorkToolCategory.Execute => Execute,
        _ => WorkPermission.Never,
    };

    public WorkPermissions With(WorkToolCategory category, WorkPermission permission) => category switch
    {
        WorkToolCategory.Read => this with { Read = permission },
        WorkToolCategory.Write => this with { Write = permission },
        WorkToolCategory.Delete => this with { Delete = permission },
        WorkToolCategory.Web => this with { Web = permission },
        WorkToolCategory.Execute => this with { Execute = permission },
        _ => this,
    };
}

public sealed record WorkContext
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = ".";
    public WorkPermissions Permissions { get; init; } = new();
}

/// <summary>
/// Whether the project's command allowlist is enforced. Null inherits System.
/// </summary>
public sealed record WorkSettings
{
    public const string CurrentSchema = "studio-work/1";

    public string Schema { get; init; } = CurrentSchema;
    public string? SystemPrompt { get; init; }
    public WorkPermissions Permissions { get; init; } = new();
    public CommandAccessMode? CommandAccess { get; init; }
    public List<string> DisabledSystemSkills { get; init; } = [];
    public List<string> EnabledSystemSkills { get; init; } = [];
    public List<string> DisabledProjectSkills { get; init; } = [];
    public List<WorkSkill> Skills { get; init; } = [];
    public List<WorkContext> Contexts { get; init; } = [];
    public List<string> DisabledTools { get; init; } = [];
    public List<string> EnabledTools { get; init; } = [];

    /// <summary>Executables the softshell and CLI wrappers may run for this project.</summary>
    public List<AllowedCommand> AllowedCommands { get; init; } = [];

    /// <summary>Inherited command names this project removes.</summary>
    public List<string> DisabledCommands { get; init; } = [];

    /// <summary>
    /// Per-tool permissions for this project. An empty set inherits the Studio
    /// defaults. The on/off lists above are the 1.1 shape and are folded into
    /// this set when an older file is loaded.
    /// </summary>
    public ToolPermissionSet ToolPermissions { get; init; } = new();
}

public sealed record WorkToolAccess(string Tool, WorkToolCategory Category, string Path)
{
    /// <summary>
    /// What exactly is being asked for when the path alone is not enough, such as
    /// the command line of a program about to run.
    /// </summary>
    public string? Detail { get; init; }
}


