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
    /// Running a program. Never by default: nothing executes in a project until
    /// someone turns it on for that folder.
    /// </summary>
    public WorkPermission Execute { get; init; } = WorkPermission.Never;

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

public sealed record WorkSettings
{
    public const string CurrentSchema = "studio-work/1";

    public string Schema { get; init; } = CurrentSchema;
    public string? SystemPrompt { get; init; }
    public WorkPermissions Permissions { get; init; } = new();
    public List<string> DisabledSystemSkills { get; init; } = [];
    public List<string> EnabledSystemSkills { get; init; } = [];
    public List<string> DisabledProjectSkills { get; init; } = [];
    public List<WorkSkill> Skills { get; init; } = [];
    public List<WorkContext> Contexts { get; init; } = [];
    public List<string> DisabledTools { get; init; } = [];
    public List<string> EnabledTools { get; init; } = [];

    /// <summary>
    /// Per-tool permissions for this project. An empty set inherits the Studio
    /// defaults. The on/off lists above are the 1.1 shape and are folded into
    /// this set when an older file is loaded.
    /// </summary>
    public ToolPermissionSet ToolPermissions { get; init; } = new();
}

public sealed record WorkToolAccess(string Tool, WorkToolCategory Category, string Path);


