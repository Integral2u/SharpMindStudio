using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpMind.Studio.Services;

public sealed record ProcessStartAlert(int ProcessId, string Name, string? Plugin, DateTimeOffset At);

/// <summary>
/// Watches for processes started underneath Studio while a tool runs. It polls
/// the process list and walks parent pids, so it needs no elevation and no ETW
/// session; the trade is that a process which starts and exits inside one tick
/// is missed, and the event arrives after the process exists.
/// </summary>
/// <remarks>
/// This is a notification, not a sandbox. Killing the process stops what is
/// still running; it cannot undo bytes already sent. Real containment needs the
/// plugin to run in a host process inside a job object.
/// </remarks>
public sealed class ProcessStartProbe : IDisposable
{
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(400);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Lock _gate = new();

    private volatile string? _plugin;

    /// <summary>Raised on the probe's background loop, not the UI thread.</summary>
    public event Action<ProcessStartAlert>? Started;

    public bool IsRunning
    {
        get { lock (_gate) return _loop is not null; }
    }

    /// <summary>Attributes anything that starts from now on to this plugin.</summary>
    public void SetPlugin(string? plugin) => _plugin = plugin;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => WatchAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _loop = null;
        }
        cts?.Cancel();
        cts?.Dispose();
    }

    public static bool TryKill(int processId, out string? error)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        var known = new HashSet<int>();
        foreach (Process process in Process.GetProcesses())
        {
            known.Add(process.Id);
            process.Dispose();
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);

                foreach (Process process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (!known.Add(process.Id)) continue;
                        if (process.Id == Environment.ProcessId) continue;
                        if (!DescendsFromStudio(process)) continue;

                        Started?.Invoke(new ProcessStartAlert(
                            process.Id,
                            SafeName(process),
                            _plugin,
                            DateTimeOffset.Now));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A process counts when any ancestor is this Studio process.</summary>
    private static bool DescendsFromStudio(Process process)
    {
        int current = process.Id;
        for (int depth = 0; depth < 8; depth++)
        {
            int parent = ParentOf(current);
            if (parent <= 0) return false;
            if (parent == Environment.ProcessId) return true;
            current = parent;
        }
        return false;
    }

    private static string SafeName(Process process)
    {
        try { return process.ProcessName; }
        catch { return "unknown"; }
    }

    private static int ParentOf(int processId)
    {
        if (!OperatingSystem.IsWindows()) return 0;

        try
        {
            using Process process = Process.GetProcessById(processId);
            var information = new ProcessBasicInformation();
            int status = NtQueryInformationProcess(
                process.Handle,
                0,
                ref information,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            return status == 0
                ? information.InheritedFromUniqueProcessId.ToInt32()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}
