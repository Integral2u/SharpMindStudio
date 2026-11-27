using System.Text.Json.Serialization;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Work;

namespace SharpMind.Studio.Core.Agents;

[JsonConverter(typeof(JsonStringEnumConverter<AgentModelKind>))]
public enum AgentModelKind
{
    Inherit,
    Local,
    Remote,
    Compute,
}

/// <summary>One fallback target in an agent's ordered model chain.</summary>
public sealed record AgentModelChoice
{
    public AgentModelKind Kind { get; init; } = AgentModelKind.Inherit;

    /// <summary>Local model path, remote model id or Compute model id.</summary>
    public string? Model { get; init; }

    /// <summary>Remote provider id or Compute host id. Null for inherit and local.</summary>
    public string? ProviderId { get; init; }
}

/// <summary>
/// A user-defined specialist the chat/work model can hand a self-contained task to.
/// <see cref="ModelChain"/> is ordered: the first available target is used, and a
/// missing local model falls through to the next entry. An empty chain means inherit.
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

    /// <summary>Legacy single local model path. Kept for spawn_agent and older files.</summary>
    public string? ModelPath { get; init; }

    /// <summary>
    /// Ordered fallback targets. Empty means inherit; a legacy <see cref="ModelPath"/>
    /// is normalized to Local then Inherit when loaded.
    /// </summary>
    public List<AgentModelChoice> ModelChain { get; init; } = [];

    public float? Temperature { get; init; }
    public int? Seed { get; init; }

    /// <summary>
    /// Optional voice for a spoken copy of this agent's reply. The chat model
    /// must be the same local speech model; null inherits the chat voice.
    /// </summary>
    public string? TtsVoice { get; init; }

    /// <summary>When true a work agent also receives the parent project's tools.</summary>
    public bool InheritTools { get; init; } = true;

    /// <summary>Tool names this agent may use. Empty means all inherited.</summary>
    public List<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// File/web access for this agent. Defaults to Ask for every category so an
    /// agent can never silently touch the disk or network.
    /// </summary>
    public WorkPermissions Permissions { get; init; } = AskPermissions;

    /// <summary>
    /// Per-tool overrides for this agent. An agent may ask for more than the
    /// session allows, but the difference is raised as a prompt rather than
    /// applied silently.
    /// </summary>
    public ToolPermissionSet ToolPermissions { get; init; } = new();

    /// <summary>Idle minutes before the loaded model may be evicted; 0 keeps the host default.</summary>
    public int TtlMinutes { get; init; } = 30;

    /// <summary>Tool calls allowed in one turn; 0 means unrestricted. Agents default to 10.</summary>
    public int MaxToolCalls { get; init; } = 10;

    public bool Enabled { get; init; } = true;

    /// <summary>Whether the specialist appears in Chat sessions.</summary>
    public bool AvailableInChat { get; init; } = true;

    /// <summary>Whether the specialist appears in Work sessions.</summary>
    public bool AvailableInWork { get; init; } = true;

    /// <summary>The effective ordered chain, with legacy fields mapped in.</summary>
    public IReadOnlyList<AgentModelChoice> EffectiveModelChain()
    {
        if (ModelChain.Count > 0)
            return [.. ModelChain.Where(IsUsableChoice)];

        if (!string.IsNullOrWhiteSpace(ModelPath))
        {
            return
            [
                new AgentModelChoice { Kind = AgentModelKind.Local, Model = ModelPath.Trim() },
                new AgentModelChoice { Kind = AgentModelKind.Inherit },
            ];
        }

        return [new AgentModelChoice { Kind = AgentModelKind.Inherit }];
    }

    private static bool IsUsableChoice(AgentModelChoice choice) =>
        choice.Kind == AgentModelKind.Inherit ||
        !string.IsNullOrWhiteSpace(choice.Model);
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
                        ToolPermissions = agent.ToolPermissions ?? new(),
                        ModelChain = NormalizeChain(agent),
                        ModelPath = FirstLocal(agent) ?? agent.ModelPath,
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
            List<AgentModelChoice> chain = NormalizeChain(agent);
            agents.Add(agent with
            {
                Id = string.IsNullOrWhiteSpace(agent.Id) ? Guid.NewGuid().ToString("n") : agent.Id.Trim(),
                Name = name,
                Description = agent.Description.Trim(),
                Rules = agent.Rules.Trim(),
                ModelPath = FirstLocal(agent with { ModelChain = chain }),
                ModelChain = chain,
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

    private static List<AgentModelChoice> NormalizeChain(StudioAgentDefinition agent)
    {
        var chain = new List<AgentModelChoice>();
        foreach (AgentModelChoice choice in agent.ModelChain ?? [])
        {
            AgentModelKind kind = choice.Kind;
            string? model = string.IsNullOrWhiteSpace(choice.Model) ? null : choice.Model.Trim();
            string? provider = string.IsNullOrWhiteSpace(choice.ProviderId)
                ? null
                : choice.ProviderId.Trim();

            if (kind != AgentModelKind.Inherit && model is null) continue;
            if (kind is AgentModelKind.Remote or AgentModelKind.Compute && provider is null) continue;
            if (chain.Any(existing => existing.Kind == kind &&
                    string.Equals(existing.Model, model, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ProviderId, provider, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            chain.Add(new AgentModelChoice
            {
                Kind = kind,
                Model = model,
                ProviderId = provider,
            });
        }

        if (chain.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(agent.ModelPath))
            {
                chain.Add(new AgentModelChoice
                {
                    Kind = AgentModelKind.Local,
                    Model = agent.ModelPath.Trim(),
                });
            }

            chain.Add(new AgentModelChoice { Kind = AgentModelKind.Inherit });
        }

        return chain;
    }

    private static string? FirstLocal(StudioAgentDefinition agent) =>
        agent.ModelChain?
            .FirstOrDefault(choice => choice.Kind == AgentModelKind.Local &&
                !string.IsNullOrWhiteSpace(choice.Model))?
            .Model;
}
