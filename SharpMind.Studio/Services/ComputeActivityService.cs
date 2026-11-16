namespace SharpMind.Studio.Services;

/// <summary>
/// Counts chat/work turns currently loading or streaming from each Compute
/// host. Scoped chat services report here so the singleton idle service can see
/// activity across circuits without capturing a circuit-scoped service.
/// </summary>
public sealed class ComputeActivityService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _chat = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public void BeginChat(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        lock (_gate)
        {
            _chat.TryGetValue(providerId, out int count);
            _chat[providerId] = count + 1;
        }

        Changed?.Invoke();
    }

    public void EndChat(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        lock (_gate)
        {
            if (!_chat.TryGetValue(providerId, out int count)) return;
            if (count <= 1) _chat.Remove(providerId);
            else _chat[providerId] = count - 1;
        }

        Changed?.Invoke();
    }

    public bool IsChatActive(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        lock (_gate) return _chat.ContainsKey(providerId);
    }
}
