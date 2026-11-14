using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Services;

namespace SharpMind.Studio.Components.Pages;

public partial class Compute : IDisposable
{
    protected override async Task OnInitializedAsync()
    {
        Dashboard.Changed += OnDashboardChanged;
        Hosts.Changed += OnComputeChanged;
        await RefreshAsync();
    }

    public void Dispose()
    {
        Dashboard.Changed -= OnDashboardChanged;
        Hosts.Changed -= OnComputeChanged;
    }

    private void OnDashboardChanged() => _ = InvokeAsync(StateHasChanged);

    private void OnComputeChanged() => _ = InvokeAsync(StateHasChanged);

    private async Task RefreshAsync()
    {
        _error = null;
        try
        {
            await Dashboard.RefreshAsync(force: true);
            _error = Dashboard.Error;
            _status = Dashboard.RunningCount == 0
                ? "No compute is billed right now."
                : $"{Dashboard.RunningCount} instance(s) running at "
                  + $"${Dashboard.HourlyCost:0.00}/hour.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
        }

        StateHasChanged();
    }

    private async Task RefreshFromPanelAsync()
    {
        await Dashboard.RefreshAsync(force: true);
        _error = Dashboard.Error;
        StateHasChanged();
    }

    private async Task DestroyAsync(VultrComputeInstance instance)
    {
        if (_busy) return;
        bool confirmed = await JS.InvokeAsync<bool>(
            "confirm",
            $"Destroy {instance.Label} ({instance.InstanceId})? The disk is deleted and billing "
            + "stops. Download any exported models first.");
        if (!confirmed) return;

        _busy = true;
        _error = null;
        _status = $"Destroying {instance.Label}...";
        StateHasChanged();
        try
        {
            await Dashboard.DestroyAsync(instance);
            _status = $"Destroyed {instance.Label}. Billing stopped.";
            await Dashboard.RefreshAsync(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _error = $"Could not destroy {instance.Label}: {ex.Message}";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task DestroyAllAsync()
    {
        if (_busy || Dashboard.RunningCount == 0) return;
        bool confirmed = await JS.InvokeAsync<bool>(
            "confirm",
            $"Destroy all {Dashboard.RunningCount} running instance(s) at "
            + $"${Dashboard.HourlyCost:0.00}/hour? Any exported models on those disks are lost.");
        if (!confirmed) return;

        _busy = true;
        _error = null;
        _status = "Destroying running instances...";
        StateHasChanged();
        try
        {
            (int destroyed, IReadOnlyList<string> failures) =
                await Dashboard.DestroyRunningAsync();
            _status = $"Destroyed {destroyed} instance(s).";
            if (failures.Count > 0)
                _error = "Some instances could not be destroyed: " + string.Join(" ", failures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private static string FormatUptime(TimeSpan? uptime)
    {
        if (uptime is not { } value || value < TimeSpan.Zero) return "-";
        if (value.TotalMinutes < 60) return $"{(int)value.TotalMinutes}m";
        if (value.TotalHours < 24) return $"{(int)value.TotalHours}h {value.Minutes}m";
        return $"{(int)value.TotalDays}d {value.Hours}h";
    }
}
