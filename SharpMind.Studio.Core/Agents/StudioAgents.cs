using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Work;

namespace SharpMind.Studio.Core.Agents;

/// <summary>
/// A user-defined specialist the chat/work model can hand a self-contained task to.
/// <see cref="ModelPath"/> null or empty means "run on the current model"; otherwise
/// the Studio loads that model for the delegation and evicts it when idle.
/// </summary>
public sealed record StudioAgentDefinition
{
    /// <summary>The safe default for a new agent and the fallback for older files.</summary>
    public static WorkPermissions AskPermissions => new()
    {
        Read = WorkPermission.Ask,
        Write = WorkPermission.Ask,
        Delete = WorkPermission.Ask,
        Web = WorkPermission.Ask,
    };

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Rules { get; init; } = "";

    /// <summary>Full model path, or null/empty to use the chat's current model.</summary>
    public string? ModelPath { get; init; }

    public float? Temperature { get; init; }
    public int? Seed { get; init; }

    /// <summary>When true a work agent also receives the parent project's tools.</summary>
    public bool InheritTools { get; init; } = true;

    /// <summary>Tool names this agent may use. Empty means all inherited.</summary>
    public List<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// File/web access for this agent. Defaults to Ask for every category so an
    /// agent can never silently touch the disk or network.
    /// </summary>
    public WorkPermissions Permissions { get; init; } = AskPermissions;

    /// <summary>Idle minutes before the loaded model may be evicted; 0 keeps the host default.</summary>
    public int TtlMinutes { get; init; } = 30;

    public bool Enabled { get; init; } = true;
}

public sealed record StudioAgentsDocument
{
    public const string CurrentSchema = "studio-agents/1";

    public string Schema { get; init; } = CurrentSchema;
    public int MaxDepth { get; init; } = 2;
    public List<StudioAgentDefinition> Agents { get; init; } = [];
}

/// <summary>Reads and writes the project's <c>agents.json</c>.</summary>
public sealed class StudioAgentStore
{
    public const string FileName = "agents.json";

    public StudioAgentStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = System.IO.Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Path = System.IO.Path.Combine(Root, FileName);
    }

    public string Root { get; }
    public string Path { get; }

    public StudioAgentsDocument Load()
    {
        if (!File.Exists(Path)) return new StudioAgentsDocument();
        try
        {
            StudioAgentsDocument document = JsonFile.Read<StudioAgentsDocument>(Path);
            return document with
            {
                Agents = [.. (document.Agents ?? [])
                    .Where(agent => !string.IsNullOrWhiteSpace(agent.Name))
                    .Select(agent => agent with
                    {
                        AllowedTools = [.. (agent.AllowedTools ?? [])],
                        Permissions = agent.Permissions ?? StudioAgentDefinition.AskPermissions,
                    })],
            };
        }
        catch
        {
            // A hand-edited or half-written file must not stop Studio opening.
            return new StudioAgentsDocument();
        }
    }

    public void Save(StudioAgentsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<StudioAgentDefinition> agents = [];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StudioAgentDefinition agent in document.Agents ?? [])
        {
            string name = agent.Name.Trim();
            if (name.Length == 0 || !names.Add(name)) continue;
            agents.Add(agent with
            {
                Id = string.IsNullOrWhiteSpace(agent.Id) ? Guid.NewGuid().ToString("n") : agent.Id.Trim(),
                Name = name,
                Description = agent.Description.Trim(),
                Rules = agent.Rules.Trim(),
                ModelPath = string.IsNullOrWhiteSpace(agent.ModelPath) ? null : agent.ModelPath.Trim(),
                AllowedTools = [.. (agent.AllowedTools ?? []).Where(tool => !string.IsNullOrWhiteSpace(tool))],
                Permissions = agent.Permissions ?? StudioAgentDefinition.AskPermissions,
                TtlMinutes = Math.Clamp(agent.TtlMinutes, 0, 24 * 60),
            });
        }

        JsonFile.Write(Path, document with
        {
            MaxDepth = Math.Clamp(document.MaxDepth, 1, 8),
            Agents = agents,
        });
    }
}
