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
    private readonly Lock _gate = new();
    private WorkSettingsStore _store;
    private WorkSettings _settings;
    private string _memory = "";
    private WorkPermissionRequest? _pending;
    private WorkChoiceRequest? _pendingChoice;

    public StudioWorkService(StudioWorkspaceService workspace, SystemWorkSkillsService systemSkills, WorkToolService tools, StudioDeviceAccess device)
    {
        _workspace = workspace;
        _systemSkills = systemSkills;
        _tools = tools;
        _device = device;
        _store = new WorkSettingsStore(workspace.Project.Root);
        _settings = _store.Load();
        _memory = _store.ReadMemory();
        MigrateLegacySkills();
        PruneMissingSkills();
        _workspace.ProjectChanged += OnProjectChanged;
    }

    public event Action? Changed;

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
        if (!string.Equals(mode, "work", StringComparison.OrdinalIgnoreCase)) return null;

        var builder = new AgentBuilder(_workspace.Project.Document.Name)
        {
            DisabledTools = [.. _tools.Tools
                .Where(tool => !IsToolEnabled(tool.Name))
                .Select(tool => tool.Name)],
        };
        if (!_device.IsMobile)
        {
            builder.WithTools(new WorkFileTools(
                _workspace.Project.Root,
                AllowedRoots(),
                CheckAccessAsync));
            builder.WithTools(new WorkWebTools(CheckAccessAsync));
            builder.WithTools(new WorkUiTools(RequestChoiceAsync));
            if (_tools.PluginTools.Count > 0)
                builder.WithTools([.. _tools.PluginTools]);
        }
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

        return builder;
    }

    public bool IsToolEnabled(string name)
    {
        if (_settings.DisabledTools.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return _tools.IsDefaultEnabled(name) ||
               _settings.EnabledTools.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
    public async Task<bool> CheckAccessAsync(WorkToolAccess access)
    {
        WorkContext? context = null;
        WorkPermission configured;
        if (access.Category == WorkToolCategory.Web)
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

        var request = new WorkPermissionRequest { Access = access };
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






