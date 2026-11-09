using SharpMind.Studio.Core.Agents;

namespace SharpMind.Studio.Services;

/// <summary>
/// Project-scoped agent definitions, persisted in <c>agents.json</c>. The same store
/// feeds the work/chat system prompt and the Agents lab editor.
/// </summary>
public sealed class StudioAgentService : IDisposable
{
    private readonly StudioWorkspaceService _workspace;
    private StudioAgentStore _store;
    private StudioAgentsDocument _document;

    public StudioAgentService(StudioWorkspaceService workspace)
    {
        _workspace = workspace;
        _store = new StudioAgentStore(workspace.Project.Root);
        _document = _store.Load();
        _workspace.ProjectChanged += OnProjectChanged;
    }

    public event Action? Changed;

    public string Path => _store.Path;
    public int MaxDepth => _document.MaxDepth;
    public IReadOnlyList<StudioAgentDefinition> Agents => _document.Agents;
    public IReadOnlyList<StudioAgentDefinition> EnabledAgents =>
        [.. _document.Agents.Where(agent => agent.Enabled)];

    public StudioAgentsDocument Document => _document;

    public StudioAgentDefinition? Find(string name) =>
        _document.Agents.FirstOrDefault(agent =>
            string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Reload()
    {
        _store = new StudioAgentStore(_workspace.Project.Root);
        _document = _store.Load();
        Changed?.Invoke();
    }

    public void Save(StudioAgentsDocument document)
    {
        _store.Save(document);
        _document = _store.Load();
        Changed?.Invoke();
    }

    public void Upsert(StudioAgentDefinition agent)
    {
        List<StudioAgentDefinition> agents = [.. _document.Agents];
        int index = string.IsNullOrWhiteSpace(agent.Id)
            ? -1
            : agents.FindIndex(item => string.Equals(item.Id, agent.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            index = agents.FindIndex(item =>
                string.Equals(item.Name, agent.Name, StringComparison.OrdinalIgnoreCase));
        }

        agent = agent with { Id = string.IsNullOrWhiteSpace(agent.Id) ? Guid.NewGuid().ToString("n") : agent.Id };
        if (index >= 0) agents[index] = agent;
        else agents.Add(agent);

        Save(_document with { Agents = agents });
    }

    public void Delete(string id)
    {
        Save(_document with
        {
            Agents = [.. _document.Agents.Where(agent =>
                !string.Equals(agent.Id, id, StringComparison.Ordinal))],
        });
    }

    public void SetMaxDepth(int depth) =>
        Save(_document with { MaxDepth = Math.Clamp(depth, 1, 8) });

    private void OnProjectChanged() => Reload();

    public void Dispose() => _workspace.ProjectChanged -= OnProjectChanged;
}
