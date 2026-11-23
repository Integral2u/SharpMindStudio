namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Keeps the current softshell directory for each Work session. It is runtime
/// state only: a new session, a new project, or a restart starts at the project
/// root, and a directory that no longer exists falls back there as well.
/// </summary>
public sealed class WorkShellDirectoryStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public string? Get(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate)
        {
            return _directories.TryGetValue(sessionId, out string? path) ? path : null;
        }
    }

    public void Set(string? sessionId, string path)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(path)) return;
        lock (_gate) _directories[sessionId] = path;
    }

    public void Reset(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        lock (_gate) _directories.Remove(sessionId);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _directories.Clear();
            _locks.Clear();
        }
    }

    /// <summary>Serialises shell calls in one session so two <c>cd</c>s cannot race.</summary>
    public SemaphoreSlim GateFor(string? sessionId)
    {
        string key = sessionId ?? "";
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[key] = gate;
            }
            return gate;
        }
    }
}

/// <summary>A session's current directory with the project/context jail applied.</summary>
public sealed class WorkShellDirectory(
    WorkShellDirectoryStore store,
    string? sessionId,
    WorkPathPolicy paths)
{
    public string Root => paths.Root;

    public string Current
    {
        get
        {
            string? stored = store.Get(sessionId);
            if (string.IsNullOrWhiteSpace(stored)) return paths.Root;

            try
            {
                return paths.ResolveDirectory(stored);
            }
            catch
            {
                // The folder was deleted or the contexts changed; do not keep a
                // stale working directory that every command would refuse.
                store.Reset(sessionId);
                return paths.Root;
            }
        }
    }

    public void Set(string path) => store.Set(sessionId, path);

    public void Reset() => store.Reset(sessionId);

    public SemaphoreSlim Gate => store.GateFor(sessionId);
}
