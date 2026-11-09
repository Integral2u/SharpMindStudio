using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
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
    private readonly Lock _gate = new();
    private WorkSettingsStore _store;
    private WorkSettings _settings;
    private string _memory = "";
    private WorkPermissionRequest? _pending;
    private WorkChoiceRequest? _pendingChoice;

    public StudioWorkService(StudioWorkspaceService workspace, SystemWorkSkillsService systemSkills, WorkToolService tools, StudioDeviceAccess device, StudioAgentService agents)
    {
        _workspace = workspace;
        _systemSkills = systemSkills;
        _tools = tools;
        _device = device;
        _agents = agents;
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

    public IAgentBuilder? CreateAgent(string mode)
    {
        bool work = string.Equals(mode, "work", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<SharpMind.Studio.Core.Agents.StudioAgentDefinition> agents = _agents.EnabledAgents;

        // Even a chat with no defined agents gets the builder for spawn_agent, so
        // the model can create a temporary specialist when the task needs one.
        var builder = new AgentBuilder(_workspace.Project.Document.Name);
        builder.DisabledTools = [.. _tools.Tools
            .Where(tool => !IsToolEnabled(tool.Name))
            .Select(tool => tool.Name)];
        if (work && !_device.IsMobile)
        {
            builder.WithTools(new WorkFileTools(
                _workspace.Project.Root,
                AllowedRoots(),
                access => CheckAccessAsync(access)));
            builder.WithTools(new WorkWebTools(access => CheckAccessAsync(access)));
            builder.WithTools(new WorkUiTools(RequestChoiceAsync));
            if (_tools.PluginTools.Count > 0)
                builder.WithTools([.. _tools.PluginTools]);
        }
        if (work)
        {
            builder.WithCustomRule(
                "You are working inside one local project. File tools are limited to the configured context locations. " +
                "Use relative paths from the project root, or an absolute path inside a context. " +
                "Never claim a file was read, created, changed or deleted unless the tool result says it succeeded.");
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

        if (agents.Count > 0)
        {
            // Studio runs every delegated agent itself so it can apply the agent's
            // model, tools and permissions. Registering engine sub-agents would
            // intercept "delegate" before that path and drop those settings.
            var names = agents
                .Select(agent => (JsonNode)JsonValue.Create(agent.Name)!)
                .ToList();
            var purposes = agents.Select(agent =>
                string.IsNullOrWhiteSpace(agent.Description)
                    ? agent.Name
                    : $"{agent.Name}: {agent.Description}");
            builder.WithTool(
                "delegate",
                "Hands a self-contained sub-task to a specialist agent and returns its answer. Use it when a listed agent's purpose matches the task better than you can do it yourself.",
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
            "Creates a temporary specialist agent for one self-contained task. Use it when no defined agent fits the request. The specialist can inherit the project's file/web tools; its permissions default to Ask.",
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

    private Task<string> InvokeAgentToolAsync(string toolName, JsonObject arguments)
    {
        Func<string, JsonObject, CancellationToken, Task<string>>? executor = AgentToolExecutor;
        return executor is null
            ? Task.FromResult("[agent runner is not available]")
            : executor(toolName, arguments, CancellationToken.None);
    }

    public bool IsToolEnabled(string name)
    {
        if (_settings.DisabledTools.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return _tools.IsDefaultEnabled(name) ||
               _settings.EnabledTools.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
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
        SharpMind.Studio.Core.Agents.StudioAgentDefinition agent)
    {
        if (_device.IsMobile) return null;
        if (!agent.InheritTools && agent.AllowedTools.Count == 0) return null;

        Task<bool> Permission(WorkToolAccess access) =>
            CheckAccessAsync(access, agent.Permissions);

        // AgentBuilder filters DisabledTools while it reflects the tool objects, so
        // discover the names first and set the filter before the real registration.
        object[] toolObjects =
        [
            new WorkFileTools(_workspace.Project.Root, AllowedRoots(), Permission),
            new WorkWebTools(Permission),
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
                !IsToolEnabled(name))],
        };
        builder.WithTools(toolObjects);
        string rules = string.IsNullOrWhiteSpace(agent.Rules)
            ? $"You are {agent.Name}. {agent.Description}".Trim()
            : agent.Rules.Trim();
        builder.WithCustomRule(rules);
        return builder;
    }

    public async Task<bool> CheckAccessAsync(WorkToolAccess access, WorkPermissions? permissions = null)
    {
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
            configured = _settings.Permissions.Web;
        }
        else
        {
            context = FindContext(access.Path);
            if (context is null) return false;
            configured = context.Permissions.For(access.Category);
        }

        if (configured == WorkPermission.Never) return false;
        if (configured == WorkPermission.Always) return true;

        var request = new WorkPermissionRequest
        {
            Access = access,
            CanRemember = permissions is null,
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
                RememberPermission(access.Category, context);
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

    private void RememberPermission(WorkToolCategory category, WorkContext? context)
    {
        if (category == WorkToolCategory.Web)
        {
            _settings = _settings with
            {
                Permissions = _settings.Permissions.With(WorkToolCategory.Web, WorkPermission.Always),
            };
        }
        else if (context is not null)
        {
            _settings = _settings with
            {
                Contexts = [.. _settings.Contexts.Select(value =>
                    value.Id == context.Id
                        ? value with { Permissions = value.Permissions.With(category, WorkPermission.Always) }
                        : value)],
            };
        }

        try { _store.Save(_settings); } catch { }
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
        Reload();
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}






