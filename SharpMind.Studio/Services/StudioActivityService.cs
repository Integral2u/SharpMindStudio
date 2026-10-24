namespace SharpMind.Studio.Services;

/// <summary>Shared "something is running" state for the shell and the dashboard.</summary>
public sealed class StudioActivityService
{
    public event Action? Changed;

    public bool IsBusy { get; private set; }
    public string? Status { get; private set; }

    public void Begin(string status)
    {
        IsBusy = true;
        Status = status;
        Changed?.Invoke();
    }

    public void Report(string status)
    {
        if (!IsBusy) return;
        Status = status;
        Changed?.Invoke();
    }

    public void End()
    {
        IsBusy = false;
        Status = null;
        Changed?.Invoke();
    }
}
