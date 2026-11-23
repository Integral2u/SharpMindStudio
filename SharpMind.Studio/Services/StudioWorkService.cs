using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Studio.Core.Agents;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Core.Vault;
using SharpMind.Studio.Core.Work;

namespace SharpMind.Studio.Services;

public enum WorkPermissionDecision
{
    Deny,
    AllowOnce,
    Always,
}

public sealed class WorkChoiceRequest
{
    private readonly TaskCompletionSource<string> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Prompt { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
    public bool AllowFreeText { get; init; }
    public Task<string> Result => _completion.Task;
    public void Resolve(string value) => _completion.TrySetResult(value);
    public void Cancel() => _completion.TrySetCanceled();
}

public sealed class WorkPermissionRequest
{
    private readonly TaskCompletionSource<WorkPermissionDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required WorkToolAccess Access { get; init; }

    /// <summary>
    /// True when "Always allow" updates the project policy. An agent's own policy
    /// is not persisted, so its prompt only offers Deny and Allow once.
    /// </summary>
    public bool CanRemember { get; init; } = true;

    /// <summary>
    /// True when an agent is asking for more than the session chain allows. The
    /// call is never widened silently: it always asks, and never remembers.
    /// </summary>
    public bool Escalation { get; init; }

    /// <summary>What the agent asked for; only meaningful when <see cref="Escalation"/> is true.</summary>
    public WorkToolPermission Wanted { get; init; }

    /// <summary>What the session chain allows; only meaningful when <see cref="Escalation"/> is true.</summary>
    public WorkToolPermission Inherited { get; init; }

    public Task<WorkPermissionDecision> Result => _completion.Task;

    public void Resolve(WorkPermissionDecision decision) => _completion.TrySetResult(decision);
}

/// <summary>
/// Project-scoped Work configuration and the Ask permission bridge between a
/// tool running on the chat loop and the Blazor circuit that can show it.
/// </summary>
public sealed class StudioWorkService : IDisposable
{
    private readonly StudioWorkspaceService _workspace;
    private readonly SystemWorkSkillsService _systemSkills;
    private readonly WorkToolService _tools;
    private readonly StudioDeviceAccess _device;
    private readonly StudioAgentService _agents;
    private readonly InferenceSettingsService _inference;
    private readonly SharpMind.Studio.Core.Mcp.McpServerHost _mcp;
    private readonly ISecretResolver _secrets;
    private readonly Lock _gate = new();
    private readonly WorkShellDirectoryStore _shells = new();
    private WorkSettingsStore _store;
    private WorkSettings _settings;
    private string _memory = "";
    private WorkPermissionRequest? _pending;
    private WorkChoiceRequest? _pendingChoice;

    /// <summary>
    /// The profile of the turn that is running. Delegated agents are created by
    /// the runner without a profile, so they inherit this one instead of
    /// silently falling back to the project defaults.
    /// </summary>
    private ToolPermissionProfile? _activeProfile;

    /// <summary>The tool-call gate for the running turn, inherited by delegated agents.</summary>
    private IToolCallFilter? _activeFilter;

    public StudioWorkService(StudioWorkspaceService workspace, SystemWorkSkillsService systemSkills, WorkToolService tools, StudioDeviceAccess device, StudioAgentService agents, InferenceSettingsService inference, SharpMind.Studio.Core.Mcp.McpServerHost mcp, ISecretResolver secrets)
    {
        _workspace = workspace;
        _systemSkills = systemSkills;
        _tools = tools;
        _device = device;
        _agents = agents;
        _inference = inference;
        _mcp = mcp;
        _secrets = secrets ?? NullSecretResolver.Instance;
        _store = new WorkSettingsStore(workspace.Project.Root);
        _settings = _store.Load();
        _memory = _store.ReadMemory();
        MigrateLegacySkills();
        PruneMissingSkills();
        _workspace.ProjectChanged += OnProjectChanged;
    }

    public event Action? Changed;

    /// <summary>
    /// Runs a <c>delegate</c> or <c>spawn_agent</c> call. StudioChatService supplies
    /// this so both the local session and an OpenRouter/Compute backend execute the
    /// same agent path with the current model and profile.
    /// </summary>
    public Func<string, JsonObject, CancellationToken, Task<string>>? AgentToolExecutor { get; set; }

    public string ProjectRoot => _store.ProjectRoot;
    public string MemoryFile => _store.MemoryFile;
    public IReadOnlyList<WorkSkill> SystemSkills => _systemSkills.Skills;

    /// <summary>The persisted softshell directory for one Work session.</summary>
    public string GetShellDirectory(string? sessionId)
    {
        var paths = new WorkPathPolicy(_workspace.Project.Root, AllowedRoots());
        return new WorkShellDirectory(_shells, sessionId, paths).Current;
    }

    public void ResetShellDirectory(string? sessionId) => _shells.Reset(sessionId);

    public WorkSettings Settings => _settings;
    public IReadOnlyList<WorkSkill> ProjectSkills => _settings.Skills;
    public IReadOnlyList<WorkToolInfo> Tools => _tools.Tools;
    public string Memory => _memory;
    public WorkPermissionRequest? PendingPermission
    {
        get
        {
            lock (_gate) return _pending;
        }
    }

    public WorkChoiceRequest? PendingChoice
    {
        get
        {
            lock (_gate) return _pendingChoice;
        }
    }

    public bool IsSystemSkillEnabled(WorkSkill skill) =>
        !_settings.DisabledSystemSkills.Contains(skill.Id, StringComparer.OrdinalIgnoreCase) &&
        (skill.DefaultEnabled ||
         _settings.EnabledSystemSkills.Contains(skill.Id, StringComparer.OrdinalIgnoreCase));

    /// <summary>Project skills are enabled unless this project explicitly disabled them.</summary>
    public bool IsProjectSkillEnabled(WorkSkill skill) =>
        !_settings.DisabledProjectSkills.Contains(skill.Id, StringComparer.OrdinalIgnoreCase) &&
        !_settings.DisabledProjectSkills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase);


    public void Reload()
    {
        _store = new WorkSettingsStore(_workspace.Project.Root);
        _settings = _store.Load();
        _memory = _store.ReadMemory();
        MigrateLegacySkills();
        PruneMissingSkills();
    }

    public void Save(WorkSettings settings, string? memory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.Save(settings);
        _store.WriteMemory(memory);
        _settings = _store.Load();
        _memory = _store.ReadMemory();
    }

    public void SaveProjectCommandLine(CommandAccessLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        layer = layer.Normalize();
        Save(_settings with
        {
            CommandAccess = layer.Mode,
            AllowedCommands = [.. layer.Commands],
            DisabledCommands = [.. layer.DisabledCommands],
        }, _memory);
        Notify();
    }

    public IAgentBuilder? CreateAgent(
        string mode,
        float? temperature = null,
        ToolPermissionProfile? profile = null,
        IToolCallFilter? filter = null,
        IReadOnlyList<object>? embeddedTools = null,
        IReadOnlyList<string>? embeddedDisabled = null,
        string? sessionId = null,
        string? familyId = null,
        string? modelPath = null,
        CommandAccessLayer? sessionCommands = null)
    {
        bool work = string.Equals(mode, "work", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<SharpMind.Studio.Core.Agents.StudioAgentDefinition> agents = _agents.EnabledAgents;

        // Remember the profile so a delegated agent created by the runner picks
        // up the same session and model layers.
        ToolPermissionProfile effective = ResolveProfile(profile);
        _activeProfile = effective;
        _activeFilter = filter;

        // Even a chat with no defined agents gets the builder for spawn_agent, so
        // the model can create a temporary specialist when the task needs one.
        // The name follows the CUI convention: the sampling temperature selects a
        // Greek tier, and an unset temperature keeps the default, Delta.
        var builder = new AgentBuilder(
            SharpMind.Studio.Core.Agents.StudioAgentNaming.ForTemperature(temperature));
        builder.DisabledTools = [.. _tools.Tools
            .Where(tool => !effective.For(tool.Name, tool.Group).IsVisible)
            .Select(tool => tool.Name)];
        builder.ToolCallFilter = filter;
        if (embeddedDisabled is { Count: > 0 })
            builder.DisabledTools.UnionWith(embeddedDisabled);
        CommandPolicy? commandPolicy = null;
        var availableWrappers = new List<string>();
        EffectiveCommandAccess commandAccess = ResolveCommandAccess(
            familyId, modelPath, sessionCommands);
        bool unrestricted = commandAccess.IsUnrestricted;
        if (work && !_device.IsMobile)
        {
            var pathPolicy = new WorkPathPolicy(_workspace.Project.Root, AllowedRoots());
            var shell = new WorkShellDirectory(_shells, sessionId, pathPolicy);
            if (!unrestricted)
                commandPolicy = new CommandPolicy(commandAccess.Commands, _workspace.Project.Root);

            var commandRunner = new WorkCommandRunner(
                commandPolicy,
                pathPolicy,
                access => CheckAccessAsync(access, profile: effective),
                _secrets,
                () => shell.Current);
            var cliRunner = new CliToolRunner(commandRunner);

            bool Available(string name) => unrestricted
                ? SharpMind.Studio.Core.Mcp.McpCommandResolver.Resolve(name) is not null
                : commandPolicy?.Contains(name) == true;

            // Wrappers first: the tool list is registration-ordered, and the
            // model should see the typed repository/build/search tools before
            // the generic built-ins and the shell. Unrestricted keeps them
            // available because they use fixed argv; the user can still turn
            // each one off under Enabled tools.
            if (Available("git"))
            {
                builder.WithTools(new GitCliTools(cliRunner));
                availableWrappers.Add("git_*");
            }
            if (Available("dotnet"))
            {
                builder.WithTools(new DotnetCliTools(cliRunner));
                availableWrappers.Add("dotnet_*");
            }
            if (Available("rg"))
            {
                builder.WithTools(new RipgrepCliTools(cliRunner));
                availableWrappers.Add("rg_*");
            }

            builder.WithTools(new WorkFileTools(
                _workspace.Project.Root,
                AllowedRoots(),
                access => CheckAccessAsync(access, profile: effective)));
            builder.WithTools(new WorkWebTools(access => CheckAccessAsync(access, profile: effective)));
            builder.WithTools(new WorkProcessTools(
                _workspace.Project.Root,
                AllowedRoots(),
                access => CheckAccessAsync(access, profile: effective),
                _secrets,
                commandPolicy,
                () => shell.Current));
            builder.WithTools(new WorkUiTools(RequestChoiceAsync));
            if (_tools.PluginTools.Count > 0)
                builder.WithTools([.. _tools.PluginTools]);
            if (embeddedTools is { Count: > 0 })
                builder.WithTools([.. embeddedTools]);

            AddMcpTools(
                builder,
                effective,
                access => CheckAccessAsync(access, profile: effective));

            if (commandPolicy is { IsEmpty: false })
            {
                builder.WithTools(new SoftShellTools(new SoftShellRunner(
                    pathPolicy,
                    commandPolicy,
                    access => CheckAccessAsync(access, profile: effective),
                    _secrets,
                    shell)));
            }
        }
        if (work)
        {
            builder.WithCustomRule(
                "You are working inside one local project. File tools are limited to the configured context locations. " +
                "Use relative paths from the project root, or an absolute path inside a context. " +
                "Never claim a file was read, created, changed or deleted unless the tool result says it succeeded.");
            if (commandPolicy is { IsEmpty: false })
            {
                string wrapperHint = availableWrappers.Count > 0
                    ? "Prefer an enabled CLI wrapper (" + string.Join(", ", availableWrappers)
                        + ") over a generic shell or built-in tool when it fits. "
                    : "";
                builder.WithCustomRule(
                    wrapperHint
                    + "Use the file tools for reading and editing files. Allowed shell commands: "
                    + commandPolicy.AllowedNames + ".");
            }
            else if (unrestricted)
            {
                builder.WithCustomRule(
                    "Command access is Unrestricted: RunProcess may start any program with the user's permissions. "
                    + "The softshell is off because it requires an allowlist. CLI wrappers remain available when their "
                    + "executable is installed, and the user can turn each one off under Enabled tools. Prefer the "
                    + "file tools and the typed wrappers for routine work.");
            }
            else
            {
                builder.WithCustomRule(
                    "No external commands are allowed in this project. Use the file tools, and do not claim a "
                    + "program ran unless a tool result says it did.");
            }
            foreach (WorkSkill skill in WorkSkillResolver.Resolve(
                         _systemSkills.Skills,
                         IsSystemSkillEnabled,
                         _settings.Skills,
                         IsProjectSkillEnabled))
            {
                builder.WithSkillContent($"## {skill.Name}\n{skill.Content}");
            }

            if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
                builder.WithAdditionalSystemPrompt(_settings.SystemPrompt.Trim());
            if (!string.IsNullOrWhiteSpace(_memory))
                builder.WithAdditionalSystemPrompt($"## Project memory\n{_memory.Trim()}");
        }

        // The delegation rules belong in the prompt, not only in the tool schema:
        // the model needs to know when to hand off and what the specialist sees.
        builder.WithAdditionalSystemPrompt(AgentPromptText.Parent(agents));

        if (agents.Count > 0)
        {
            // Studio runs every delegated agent itself so it can apply the agent's
            // model, tools and permissions. Registering engine sub-agents would
            // intercept "delegate" before that path and drop those settings.
            var names = agents                .Select(agent => (JsonNode)JsonValue.Create(agent.Name)!)
                .ToList();
            var purposes = agents.Select(agent =>
                string.IsNullOrWhiteSpace(agent.Description)
                    ? agent.Name
                    : $"{agent.Name}: {agent.Description}");
            builder.WithTool(
                "delegate",
                "Hands a self-contained sub-task to one of the saved specialist agents by name and returns its answer. The agent's stored rules, model, tools and permissions are used. Use it when a listed agent's purpose matches the task better than you can do it yourself.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["agent"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray([.. names]),
                            ["description"] = string.Join("; ", purposes),
                        },
                        ["task"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The complete sub-task, with all the context the agent needs.",
                        },
                    },
                    ["required"] = new JsonArray("agent", "task"),
                },
                arguments => InvokeAgentToolAsync("delegate", arguments));
        }

        // The model can also create a temporary specialist when none of the
        // defined agents fits the task. The call is handled by StudioChatService;
        // the nested run gets the agent tools, not this tool again.
        builder.WithTool(
            "spawn_agent",
            "Creates a temporary specialist for one self-contained task. Nothing is saved: name, rules, optional model, optional tool list and optional tool-call budget come from this call. Use it when no defined agent fits. The specialist inherits the enabled project tools and its file/web permissions default to Ask.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "A short name for the specialist.",
                    },
                    ["purpose"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "One line describing what the specialist is for.",
                    },
                    ["rules"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The specialist's system prompt, behaviour and constraints.",
                    },
                    ["model"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional model path. Omit to use the current model.",
                    },
                    ["allowed_tools"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Optional tool names; omit to inherit the enabled tools.",
                    },
                    ["max_tool_calls"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["maximum"] = 1000,
                        ["description"] = "Optional tool-call budget for this specialist; 0 means unrestricted. Defaults to 10.",
                    },
                    ["task"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The complete self-contained task.",
                    },
                },
                ["required"] = new JsonArray("name", "rules", "task"),
            },
            arguments => InvokeAgentToolAsync("spawn_agent", arguments));

        return builder;
    }

    /// <summary>
    /// Adds the MCP tools the user has explicitly allowed to a builder. A
    /// server's own code cannot be gated per call, so consent is an explicit
    /// project (or session) entry: a default inherited value keeps the tool out
    /// of the prompt. <paramref name="permission"/> is the same gate the built-in
    /// tools use, so Never refuses and Ask prompts. <paramref name="allowed"/>
    /// lets an agent narrow the set with its own allow list; it can never widen
    /// it.
    /// </summary>
    private void AddMcpTools(
        AgentBuilder builder,
        ToolPermissionProfile effective,
        Func<WorkToolAccess, Task<bool>> permission,
        IReadOnlySet<string>? allowed = null)
    {
        foreach (WorkToolInfo tool in _tools.Tools.Where(tool => tool.IsMcp))
        {
            if (allowed is not null && !allowed.Contains(tool.Name)) continue;

            ToolPermissionValue permissionValue = effective.For(tool.Name, tool.Group);
            if (permissionValue.Permission == WorkToolPermission.Off) continue;

            // Consent lives below the agent layer. An agent that asks for an MCP
            // tool the user never granted must not get it, so resolve the chain
            // without the agent's own entry.
            ToolPermissionValue inherited = (effective with { Agent = null })
                .For(tool.Name, tool.Group);
            if (inherited.Permission == WorkToolPermission.Off ||
                inherited.Source == ToolPermissionResolver.DefaultSource)
            {
                continue;
            }

            if (!TrySplitMcpName(tool.Name, out string serverId, out string mcpTool)) continue;

            McpToolInfo? info = _mcp.Connected
                .FirstOrDefault(server =>
                    server.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase))
                ?.Tools.FirstOrDefault(candidate => candidate.Name == mcpTool);
            if (info is null) continue;

            // The schema is cached with the connected server and shared by every
            // builder. A JsonNode can only have one parent, so hand the builder a
            // clone; without this the second chat turn throws while registering.
            var schema = info.InputSchema is JsonObject input
                ? (JsonObject)input.DeepClone()
                : new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                };

            builder.WithTool(
                tool.Name,
                info.Description,
                schema,
                arguments => CallMcpToolAsync(
                    serverId, mcpTool, tool.Name, arguments, permission));
        }
    }

    private static bool TrySplitMcpName(string name, out string serverId, out string tool)
    {
        serverId = tool = "";
        string[] parts = name.Split("__", 3);
        if (parts.Length != 3 || !parts[0].Equals("mcp", StringComparison.Ordinal)) return false;

        serverId = parts[1];
        tool = parts[2];
        return serverId.Length > 0 && tool.Length > 0;
    }

    private async Task<string> CallMcpToolAsync(
        string serverId,
        string tool,
        string toolName,
        JsonObject arguments,
        Func<WorkToolAccess, Task<bool>> permission)
    {
        IReadOnlyList<string> secretNames = SecretTemplate.Names(arguments);
        string detail = $"{serverId}/{tool}";
        if (secretNames.Count > 0)
            detail += $" (uses secret: {string.Join(", ", secretNames)})";

        var access = new WorkToolAccess(
            toolName,
            WorkToolCategory.Execute,
            _workspace.Project.Root)
        {
            Detail = detail,
        };

        if (!await permission(access).ConfigureAwait(false))
            return "Permission denied for this MCP tool.";

        try
        {
            McpToolResult result = await _mcp
                .CallAsync(serverId, tool, arguments)
                .ConfigureAwait(false);

            return result.IsError ? $"MCP error: {result.Text}" : result.Text;
        }
        catch (SecretMissingException ex)
        {
            return ex.Message;
        }
    }

    private Task<string> InvokeAgentToolAsync(string toolName, JsonObject arguments)    {
        Func<string, JsonObject, CancellationToken, Task<string>>? executor = AgentToolExecutor;
        return executor is null
            ? Task.FromResult("[agent runner is not available]")
            : executor(toolName, arguments, CancellationToken.None);
    }

    public bool IsToolEnabled(string name)
    {
        if (_settings.ToolPermissions.TryTool(name, out WorkToolPermission value))
            return value != WorkToolPermission.Off;
        if (_settings.DisabledTools.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return _tools.IsDefaultEnabled(name) ||
               _settings.EnabledTools.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fills the project and system layers when the caller only supplied the
    /// session, model and family. The project layer sits above the Studio
    /// defaults, because a project toggle is a deliberate choice.
    /// </summary>
    private ToolPermissionProfile ResolveProfile(ToolPermissionProfile? profile)
    {
        profile ??= new ToolPermissionProfile();
        return profile with
        {
            // Always read the live project layer: "Always allow" during a turn
            // changes it, and the next tool call in the same turn must see it.
            Project = ProjectToolLayer(),
            System = profile.System ?? _inference.SystemTools,
        };
    }

    /// <summary>
    /// The project's per-tool permissions expressed as the four-state model.
    /// Tools the project never mentions keep inheriting, so a system default of
    /// Off is not turned back on by an implicit enable.
    /// </summary>
    public ToolPermissionSet ProjectToolLayer()
    {
        var layer = new ToolPermissionSet();
        foreach (WorkToolInfo tool in _tools.Tools)
        {
            if (_settings.ToolPermissions.TryTool(tool.Name, out WorkToolPermission value))
            {
                layer.Tools[tool.Name] = value;
                continue;
            }

            // A tool added after the file migrated still has its legacy switch.
            if (_settings.DisabledTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            {
                layer.Tools[tool.Name] = WorkToolPermission.Off;
                continue;
            }

            if (_settings.EnabledTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
                layer.Tools[tool.Name] = WorkToolPermission.Ask;
        }

        // The old Web toggle is the network gate. Seed FetchUrl from it so the
        // tool control is authoritative without turning network access on.
        if (!layer.Tools.ContainsKey("FetchUrl"))
        {
            layer.Tools["FetchUrl"] =
                ToolPermissionResolver.FromWorkPermission(_settings.Permissions.Web);
        }

        return layer;
    }

    public CommandAccessLayer ProjectCommandLayer() => new()
    {
        Mode = _settings.CommandAccess,
        Commands = [.. _settings.AllowedCommands],
        DisabledCommands = [.. _settings.DisabledCommands],
    };

    /// <summary>
    /// Resolves the command layers for one session. The project layer is read
    /// live, so a settings change applies to the next turn.
    /// </summary>
    public EffectiveCommandAccess ResolveCommandAccess(
        string? familyId,
        string? modelPath,
        CommandAccessLayer? session)
    {
        return CommandAccessResolver.Resolve(
            _inference.SystemCommands,
            ProjectCommandLayer(),
            _inference.FamilyCommands(familyId),
            _inference.ModelCommands(modelPath),
            session);
    }

    /// <summary>The inherited command layer for one settings editor.</summary>
    public EffectiveCommandAccess InheritedCommandAccess(
        string scope,
        string? familyId = null,
        string? modelPath = null)
    {
        CommandAccessLayer project = ProjectCommandLayer();
        CommandAccessLayer? family = _inference.FamilyCommands(familyId);
        CommandAccessLayer? model = _inference.ModelCommands(modelPath);
        return scope switch
        {
            "system" => CommandAccessResolver.Resolve(null),
            "project" => CommandAccessResolver.Resolve(_inference.SystemCommands),
            "family" => CommandAccessResolver.Resolve(
                _inference.SystemCommands, project),
            "model" => CommandAccessResolver.Resolve(
                _inference.SystemCommands, project, family),
            "session" => CommandAccessResolver.Resolve(
                _inference.SystemCommands, project, family, model),
            _ => CommandAccessResolver.Resolve(
                _inference.SystemCommands, project, family, model),
        };
    }

    /// <summary>The project-and-system value for a tool, for the settings panel. Session, model, family and agent overrides are deliberately not included.</summary>
    public WorkToolPermission ProjectToolPermission(string name) =>
        ResolveProfile(null).For(name, _tools.GroupFor(name)).Permission;

    /// <summary>What a project override would inherit from: the Studio system layer.</summary>
    public ToolPermissionValue SystemToolPermission(string tool, string? group) =>
        ToolPermissionResolver.Resolve(tool, group, system: _inference.SystemTools);
    public void SetToolEnabled(string name, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<string> enabledTools = [.. _settings.EnabledTools];
        List<string> disabledTools = [.. _settings.DisabledTools];
        if (enabled)
        {
            disabledTools.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            if (!_tools.IsDefaultEnabled(name) &&
                !enabledTools.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                enabledTools.Add(name);
            }
        }
        else
        {
            enabledTools.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            if (!disabledTools.Contains(name, StringComparer.OrdinalIgnoreCase))
                disabledTools.Add(name);
        }

        Save(_settings with { EnabledTools = enabledTools, DisabledTools = disabledTools }, _memory);
        Notify();
    }

    /// <summary>
    /// A tool builder for one agent. It exposes the project's file/web tools with
    /// the agent's own Ask/Allow/Deny policy, then filters them to the enabled and
    /// allowed set. Defined agents are deliberately not registered here, so a
    /// delegated agent cannot recurse through the delegate tool.
    /// </summary>
    public IAgentBuilder? CreateAgentTools(
        SharpMind.Studio.Core.Agents.StudioAgentDefinition agent,
        ToolPermissionProfile? profile = null,
        string? sessionId = null,
        string? familyId = null,
        string? modelPath = null,
        CommandAccessLayer? sessionCommands = null)
    {
        if (_device.IsMobile) return null;
        if (!agent.InheritTools && agent.AllowedTools.Count == 0) return null;

        // The agent's own tool set is the most specific layer. Anything it does
        // not mention inherits from the turn's profile.
        ToolPermissionProfile effective = ResolveProfile(
            (profile ?? _activeProfile ?? new ToolPermissionProfile())
            with { Agent = agent.ToolPermissions });

        Task<bool> Permission(WorkToolAccess access) =>
            CheckAccessAsync(access, agent.Permissions, effective);

        EffectiveCommandAccess commandAccess = ResolveCommandAccess(
            familyId, modelPath, sessionCommands);
        bool unrestricted = commandAccess.IsUnrestricted;
        CommandPolicy? commandPolicy = null;
        var pathPolicy = new WorkPathPolicy(_workspace.Project.Root, AllowedRoots());
        var shell = new WorkShellDirectory(_shells, sessionId, pathPolicy);
        if (!unrestricted)
            commandPolicy = new CommandPolicy(commandAccess.Commands, _workspace.Project.Root);

        var commandRunner = new WorkCommandRunner(
            commandPolicy, pathPolicy, Permission, _secrets, () => shell.Current);
        var cliRunner = new CliToolRunner(commandRunner);

        bool Available(string name) => unrestricted
            ? SharpMind.Studio.Core.Mcp.McpCommandResolver.Resolve(name) is not null
            : commandPolicy?.Contains(name) == true;

        var wrapperTools = new List<object>();
        var availableWrappers = new List<string>();
        if (Available("git"))
        {
            wrapperTools.Add(new GitCliTools(cliRunner));
            availableWrappers.Add("git_*");
        }
        if (Available("dotnet"))
        {
            wrapperTools.Add(new DotnetCliTools(cliRunner));
            availableWrappers.Add("dotnet_*");
        }
        if (Available("rg"))
        {
            wrapperTools.Add(new RipgrepCliTools(cliRunner));
            availableWrappers.Add("rg_*");
        }
        if (commandPolicy is { IsEmpty: false })
        {
            wrapperTools.Add(new SoftShellTools(new SoftShellRunner(
                pathPolicy, commandPolicy, Permission, _secrets, shell)));
        }

        // AgentBuilder filters DisabledTools while it reflects the tool objects, so
        // discover the names first and set the filter before the real registration.
        object[] toolObjects =
        [
            .. wrapperTools,
            new WorkFileTools(_workspace.Project.Root, AllowedRoots(), Permission),
            new WorkWebTools(Permission),
            new WorkProcessTools(
                _workspace.Project.Root,
                AllowedRoots(),
                Permission,
                _secrets,
                commandPolicy,
                () => shell.Current),
            .. _tools.PluginTools,
        ];
        var probe = new AgentBuilder(agent.Name);
        probe.WithTools(toolObjects);
        var allowed = agent.AllowedTools.Count > 0
            ? new HashSet<string>(agent.AllowedTools, StringComparer.OrdinalIgnoreCase)
            : null;

        var builder = new AgentBuilder(agent.Name)
        {
            DisabledTools = [.. probe.RegisteredToolNames.Where(name =>
                (allowed is not null && !allowed.Contains(name)) ||
                !effective.For(name, _tools.GroupFor(name)).IsVisible)],
            ToolCallFilter = _activeFilter,
        };
        builder.WithTools(toolObjects);
        // MCP is a process, so the project's Run permission is the consent gate,
        // the same check Connect uses. The agent's own tool layer and allow list
        // still narrow which servers and tools it can reach.
        AddMcpTools(
            builder,
            effective,
            access => CheckAccessAsync(access, profile: effective),
            allowed);
        string rules = string.IsNullOrWhiteSpace(agent.Rules)
            ? $"You are {agent.Name}. {agent.Description}".Trim()
            : agent.Rules.Trim();
        builder.WithCustomRule(rules);
        if (commandPolicy is { IsEmpty: false })
        {
            string wrapperHint = availableWrappers.Count > 0
                ? "Prefer an enabled CLI wrapper (" + string.Join(", ", availableWrappers)
                    + ") over the generic shell or a built-in tool when it fits. "
                : "";
            builder.WithCustomRule(
                wrapperHint
                + "Use the file tools for reading and editing files. Allowed shell commands: "
                + commandPolicy.AllowedNames + ".");
        }
        else if (unrestricted)
        {
            builder.WithCustomRule(
                "Command access is Unrestricted: RunProcess may start any program with the user's permissions. "
                + "The softshell is off because it requires an allowlist. CLI wrappers remain available when installed. "
                + "Prefer the file tools and the typed wrappers for routine work.");
        }
        else
        {
            builder.WithCustomRule(
                "No external commands are allowed in this project. Use the file tools.");
        }
        return builder;
    }

    /// <summary>
    /// The engine's IO gate callback. File access is treated as a write (the
    /// context does not distinguish reads) and network as web; both then go
    /// through the folder rules.
    /// </summary>
    public async Task<ToolPermission> CheckToolPermissionAsync(ToolPermissionContext permission)
    {
        ArgumentNullException.ThrowIfNull(permission);

        WorkToolCategory category = permission.Category == ToolCategory.Network
            ? WorkToolCategory.Web
            : WorkToolCategory.Write;

        bool allowed = await CheckAccessAsync(new WorkToolAccess(
            permission.ToolName,
            category,
            permission.Resource)).ConfigureAwait(false);

        return allowed ? ToolPermission.Always : ToolPermission.Never;
    }

    public async Task<bool> CheckAccessAsync(
        WorkToolAccess access,
        WorkPermissions? permissions = null,
        ToolPermissionProfile? profile = null,
        ToolPermissionSet? agentTools = null)
    {
        if (agentTools is not null)
            profile = (profile ?? new ToolPermissionProfile()) with { Agent = agentTools };

        ToolPermissionProfile effectiveProfile = ResolveProfile(profile);
        string? group = _tools.GroupFor(access.Tool);
        ToolPermissionValue tool = effectiveProfile.For(access.Tool, group);
        if (tool.Permission == WorkToolPermission.Off) return false;

        WorkContext? context = null;
        WorkPermission configured;
        if (permissions is not null)
        {
            configured = permissions.For(access.Category);
            if (access.Category != WorkToolCategory.Web)
            {
                context = FindContext(access.Path);
                if (context is null) return false;
            }
        }
        else if (access.Category == WorkToolCategory.Web)
        {
            // FetchUrl carries the network policy in the tool layer, seeded from
            // the old Web toggle, so the folder cannot double-gate it.
            configured = WorkPermission.Always;
        }
        else
        {
            context = FindContext(access.Path);
            if (context is null) return false;
            configured = context.Permissions.For(access.Category);
        }

        // A folder set to Never is a hard boundary. Otherwise the tool decides,
        // so setting a tool to Always is not undone by a folder that only asks.
        WorkToolPermission effective = ToolPermissionResolver.ApplyFolder(tool.Permission, configured);

        // An agent that asks for more than the session allows must ask, even when
        // its own policy is Always.
        if (tool.Escalates) effective = WorkToolPermission.Ask;

        if (effective == WorkToolPermission.Never) return false;
        if (effective == WorkToolPermission.Always) return true;

        var request = new WorkPermissionRequest
        {
            Access = access,
            CanRemember = permissions is null && !tool.Escalates,
            Escalation = tool.Escalates,
            Wanted = tool.Permission,
            Inherited = tool.Escalates
                ? effectiveProfile.InheritedFor(access.Tool, group)
                : effective,
        };
        lock (_gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException("A Work permission request is already awaiting an answer.");
            _pending = request;
        }
        Notify();

        WorkPermissionDecision decision = await request.Result.ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(_pending, request)) _pending = null;
        }
        Notify();

        if (decision == WorkPermissionDecision.Always)
        {
            // An agent's policy must not silently become the project policy.
            if (permissions is null)
                RememberToolPermission(access.Tool);
            return true;
        }

        return decision == WorkPermissionDecision.AllowOnce;
    }

    public async Task<string> RequestChoiceAsync(
        string prompt,
        IReadOnlyList<string> options,
        bool allowFreeText)
    {
        var request = new WorkChoiceRequest
        {
            Prompt = prompt,
            Options = options,
            AllowFreeText = allowFreeText,
        };
        lock (_gate)
        {
            if (_pendingChoice is not null)
                throw new InvalidOperationException("A Work choice request is already awaiting an answer.");
            _pendingChoice = request;
        }
        Notify();

        try
        {
            return await request.Result.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingChoice, request)) _pendingChoice = null;
            }
            Notify();
        }
    }

    public Task ResolveChoiceAsync(string value)
    {
        WorkChoiceRequest? request;
        lock (_gate)
        {
            request = _pendingChoice;
            _pendingChoice = null;
        }
        request?.Resolve(value);
        Notify();
        return Task.CompletedTask;
    }

    public void CancelChoice()
    {
        WorkChoiceRequest? request;
        lock (_gate)
        {
            request = _pendingChoice;
            _pendingChoice = null;
        }
        request?.Cancel();
        Notify();
    }
    public Task ResolvePermissionAsync(WorkPermissionDecision decision)
    {
        WorkPermissionRequest? request;
        lock (_gate)
        {
            request = _pending;
            _pending = null;
        }

        request?.Resolve(decision);
        Notify();
        return Task.CompletedTask;
    }

    public void CancelPermission()
    {
        WorkPermissionRequest? request;
        lock (_gate)
        {
            request = _pending;
            _pending = null;
        }

        request?.Resolve(WorkPermissionDecision.Deny);
        Notify();
    }

    public void Dispose() => _workspace.ProjectChanged -= OnProjectChanged;

    public string ResolveContextPath(WorkContext context)
        => WorkContextValidation.Resolve(context, _store.ProjectRoot);

    private List<string> AllowedRoots()
    {
        var roots = _settings.Contexts
            .Select(ResolveContextPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0) roots.Add(_store.ProjectRoot);
        return roots;
    }

    private WorkContext? FindContext(string path)
    {
        string full = Path.GetFullPath(path);
        return _settings.Contexts
            .Select(context => (Context: context, Root: ResolveContextPath(context)))
            .Where(pair => IsUnder(full, pair.Root))
            .OrderByDescending(pair => pair.Root.Length)
            .Select(pair => pair.Context)
            .FirstOrDefault();
    }

    /// <summary>
    /// "Always allow" raises the project's tool entry, so the Tools panel shows
    /// the new value and the next call does not ask again. Folder permissions
    /// are edited in their own grid and are never widened behind the user's
    /// back.
    /// </summary>
    private void RememberToolPermission(string tool)
    {
        var tools = new Dictionary<string, WorkToolPermission>(
            _settings.ToolPermissions.Tools, StringComparer.OrdinalIgnoreCase);
        tools[tool] = WorkToolPermission.Always;
        _settings = _settings with
        {
            ToolPermissions = new ToolPermissionSet
            {
                Tools = tools,
                Groups = _settings.ToolPermissions.Groups,
            },
        };
        try { _store.Save(_settings); } catch { }
        Notify();
    }

    private void MigrateLegacySkills()
    {
        bool changed = false;
        var skills = _settings.Skills.ToList();

        // Project skills now stay with the project. Older builds migrated them
        // into the shared repository; those projects already have an empty list,
        // so this only gives a stable identity to anything still here.
        for (int i = 0; i < skills.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(skills[i].Id)) continue;
            skills[i] = skills[i] with { Id = Guid.NewGuid().ToString("n") };
            changed = true;
        }

        string legacy = _store.ReadSkills();
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            string name = "Project skill";
            if (skills.Any(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = "Project skill (legacy)";

            skills.Add(new WorkSkill
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = name,
                Content = legacy.Trim(),
                DefaultEnabled = true,
            });
            changed = true;
        }

        if (changed)
        {
            _settings = _settings with { Skills = skills };
            try { _store.Save(_settings); } catch { }
        }
        if (!string.IsNullOrWhiteSpace(legacy)) _store.RetireLegacySkillsFile();
    }


    private void PruneMissingSkills()
    {
        var ids = _systemSkills.Skills
            .Select(skill => skill.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> disabled = [.. _settings.DisabledSystemSkills.Where(ids.Contains)];
        List<string> enabled = [.. _settings.EnabledSystemSkills.Where(ids.Contains)];

        var projectIds = _settings.Skills
            .Select(skill => skill.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> disabledProject = [.. _settings.DisabledProjectSkills.Where(id => projectIds.Contains(id))];

        if (disabled.Count == _settings.DisabledSystemSkills.Count &&
            enabled.Count == _settings.EnabledSystemSkills.Count &&
            disabledProject.Count == _settings.DisabledProjectSkills.Count)
            return;

        _settings = _settings with
        {
            DisabledSystemSkills = disabled,
            EnabledSystemSkills = enabled,
            DisabledProjectSkills = disabledProject,
        };
        try { _store.Save(_settings); } catch { }
    }
    private static bool IsUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private void OnProjectChanged()
    {
        _shells.Clear();
        Reload();
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}






