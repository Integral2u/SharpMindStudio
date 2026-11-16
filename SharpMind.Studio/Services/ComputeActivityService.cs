namespace SharpMind.Studio.Services;

/// <summary>
/// Counts chat/work turns and long operations (deploy, setup, upload) currently
/// using each Compute host. Scoped chat services report here so the singleton
/// idle service can see activity across circuits without capturing a
/// circuit-scoped service.
/// </summary>
public sealed class ComputeActivityService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _chat = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _operations = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public void BeginChat(string? providerId) => Add(_chat, providerId);

    public void EndChat(string? providerId) => Remove(_chat, providerId);

    public void BeginOperation(string? providerId) => Add(_operations, providerId);

    public void EndOperation(string? providerId) => Remove(_operations, providerId);

    public bool IsChatActive(string? providerId) => Contains(_chat, providerId);

    public bool IsActive(string? providerId) =>
        Contains(_chat, providerId) || Contains(_operations, providerId);

    private void Add(Dictionary<string, int> counts, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        lock (_gate)
        {
            counts.TryGetValue(providerId, out int count);
            counts[providerId] = count + 1;
        }

        Changed?.Invoke();
    }

    private void Remove(Dictionary<string, int> counts, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        lock (_gate)
        {
            if (!counts.TryGetValue(providerId, out int count)) return;
            if (count <= 1) counts.Remove(providerId);
            else counts[providerId] = count - 1;
        }

        Changed?.Invoke();
    }

    private bool Contains(Dictionary<string, int> counts, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        lock (_gate) return counts.ContainsKey(providerId);
    }
}
