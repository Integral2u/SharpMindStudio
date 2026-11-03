namespace SharpMind.Studio.Services;

public enum PathPickerMode
{
    Folder,
    File,
    MultiFile,
    SaveFile,
}

public sealed record PathPickerRequest(
    PathPickerMode Mode,
    string? StartPath,
    string? Title,
    string SearchPattern);

public sealed record PathPickerSelection(string? Path, IReadOnlyList<string> Paths)
{
    public IReadOnlyList<string> All =>
        Paths.Count > 0 ? Paths : Path is null ? [] : [Path];

    public static PathPickerSelection Folder(string path) => new(path, []);

    public static PathPickerSelection Files(IReadOnlyList<string> paths) =>
        new(paths.Count == 1 ? paths[0] : null, paths);
}

/// <summary>
/// One path dialog per circuit. A component asks for a path and awaits the
/// result; the dialog is rendered once by the main layout.
/// </summary>
public sealed class PathPickerService
{
    private TaskCompletionSource<PathPickerSelection?>? _pending;

    public PathPickerRequest? Current { get; private set; }

    public event Action? Changed;

    public Task<PathPickerSelection?> PickAsync(
        PathPickerMode mode,
        string? startPath = null,
        string? title = null,
        string searchPattern = "*.*")
    {
        // Only one dialog can be open per circuit.
        if (_pending is not null) return Task.FromResult<PathPickerSelection?>(null);

        _pending = new TaskCompletionSource<PathPickerSelection?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Current = new PathPickerRequest(
            mode,
            string.IsNullOrWhiteSpace(startPath) ? null : startPath.Trim(),
            title,
            string.IsNullOrWhiteSpace(searchPattern) ? "*.*" : searchPattern);
        Changed?.Invoke();
        return _pending.Task;
    }

    public void Complete(PathPickerSelection? selection)
    {
        TaskCompletionSource<PathPickerSelection?>? pending = _pending;
        _pending = null;
        Current = null;
        Changed?.Invoke();
        pending?.TrySetResult(selection);
    }
}
