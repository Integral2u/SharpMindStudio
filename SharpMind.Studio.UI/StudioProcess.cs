using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Text;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.UI;

/// <summary>
/// Owns the local Studio web host. If a healthy server is already listening we
/// attach to it instead of starting a second copy.
/// </summary>
internal sealed class StudioProcess : IAsyncDisposable
{
    public StudioProcess()
    {
        (string host, int port) = ReadConnection();
        try
        {
            BaseUri = new Uri($"http://{host}:{port}/");
            IsRemote = !IsLocalHost(host);
        }
        catch
        {
            BaseUri = new Uri("http://127.0.0.1:5117/");
            IsRemote = false;
        }
    }

    public Uri BaseUri { get; private set; }
    public bool IsRemote { get; private set; }

    public void UseLocalDefaults()
    {
        BaseUri = new Uri("http://127.0.0.1:5117/");
        IsRemote = false;
    }

    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _stdout = new();
    private Process? _process;
    private bool _ownsProcess;

    public bool IsOwned => _ownsProcess;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
            return;

        if (IsRemote)
            throw new RemoteStudioUnavailableException(BaseUri);

        var startInfo = ResolveStartInfo();
        startInfo.Environment["Studio__Url"] = BaseUri.ToString().TrimEnd('/');
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        _process.OutputDataReceived += (_, e) => Append(_stdout, e.Data);
        _process.ErrorDataReceived += (_, e) => Append(_stderr, e.Data);

        if (!_process.Start())
            throw new InvalidOperationException("Could not start the Studio web host.");
        _ownsProcess = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
                throw new InvalidOperationException(
                    $"Studio exited with code {_process.ExitCode}.{Environment.NewLine}{Tail(_stderr)}");

            if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                return;

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        await StopAsync().ConfigureAwait(false);
        throw new TimeoutException(
            $"Studio did not become ready at {BaseUri}.{Environment.NewLine}{Tail(_stderr)}");
    }

    public async Task StopAsync()
    {
        Process? process = _process;
        _process = null;
        _ownsProcess = false;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Shutdown must not fail because the child already vanished.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task StopLocalServerAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (IsRemote) return;

        int port = BaseUri.Port;
        try
        {
            var startInfo = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(startInfo)!;
            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            foreach (string line in output.Split('\n'))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[1].EndsWith($":{port}", StringComparison.Ordinal)) continue;
                if (!parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(parts[4], out int pid) || pid == Environment.ProcessId) continue;

                try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
                catch { }
            }
        }
        catch
        {
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Reads running compute from the local Studio host. Returns null when the
    /// host cannot be reached or nothing is running, so the exit path stays quiet
    /// unless there is something that costs money.
    /// </summary>
    public async Task<RunningComputeSummary?> GetRunningComputeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client
                .GetAsync(new Uri(BaseUri, "compute/api/running"), cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            RunningComputePayload? payload = JsonSerializer
                .Deserialize<RunningComputePayload>(json, ComputeJson);
            if (payload is null || payload.Count <= 0 || payload.Instances.Count == 0)
                return null;

            return new RunningComputeSummary(
                payload.Count, payload.HourlyCost, payload.EstimatedCost, payload.Instances);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Destroys every running instance; used by Destroy and exit.</summary>
    public async Task<(bool Ok, string Message)> DestroyRunningComputeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var response = await client
                .PostAsync(new Uri(BaseUri, "compute/api/destroy-running"), null, cancellationToken)
                .ConfigureAwait(false);
            string json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Vultr teardown returned HTTP {(int)response.StatusCode}.");
            }

            DestroyRunningPayload? payload = JsonSerializer
                .Deserialize<DestroyRunningPayload>(json, ComputeJson);
            if (payload is null) return (false, "Vultr teardown returned no result.");

            if (payload.Failures.Count == 0)
                return (true, $"Destroyed {payload.Destroyed} instance(s). Billing stopped.");

            return (false,
                $"Destroyed {payload.Destroyed} instance(s), but {payload.Failures.Count} failed: "
                + string.Join(" ", payload.Failures));
        }
        catch (Exception ex)
        {
            return (false, $"Could not destroy compute: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions ComputeJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record RunningComputePayload(
        int Count,
        double HourlyCost,
        double EstimatedCost,
        List<RunningComputeItem> Instances);

    private sealed record DestroyRunningPayload(int Destroyed, List<string> Failures);

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1200) };
            using var response = await client.GetAsync(BaseUri, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static (string Host, int Port) ReadConnection()
    {
        string host = "127.0.0.1";
        int port = 5117;
        try
        {
            if (File.Exists(StudioPaths.SettingsFile))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(StudioPaths.SettingsFile));
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("studioHost", out JsonElement hostElement) &&
                    hostElement.GetString() is { Length: > 0 } configuredHost)
                    host = configuredHost.Trim();
                if (root.TryGetProperty("studioPort", out JsonElement portElement) &&
                    portElement.TryGetInt32(out int configuredPort) &&
                    configuredPort is > 0 and <= 65535)
                    port = configuredPort;
            }
        }
        catch
        {
        }
        return (host, port);
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    private static ProcessStartInfo ResolveStartInfo()
    {
        string baseDirectory = AppContext.BaseDirectory;

        // Published layout: the web host sits beside the wrapper. Require the
        // managed DLL too; a ProjectReference can copy the apphost alone, and
        // launching that apphost without its DLL just produces a silent exit.
        string publishedExe = Path.Combine(baseDirectory, "SharpMind.Studio.exe");
        string publishedDll = Path.Combine(baseDirectory, "SharpMind.Studio.dll");
        if (File.Exists(publishedExe) && File.Exists(publishedDll))
            return ForExecutable(publishedExe, baseDirectory);
        if (File.Exists(publishedDll))
            return ForDll(publishedDll, baseDirectory);

        string? root = FindRepositoryRoot(baseDirectory);
        if (root is null)
            throw new FileNotFoundException(
                "Could not find SharpMind.Studio. Publish the web host beside SharpMind.Studio.UI.exe " +
                "or run the wrapper from the repository.");

        string projectDirectory = Path.Combine(root, "SharpMind.Studio");
        string? dll = FindBuiltDll(projectDirectory);
        if (dll is not null)
            return ForDll(dll, projectDirectory);

        string project = Path.Combine(projectDirectory, "SharpMind.Studio.csproj");
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(project);
        return info;
    }

    private static ProcessStartInfo ForExecutable(string exe, string workingDirectory) =>
        new(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

    private static ProcessStartInfo ForDll(string dll, string workingDirectory)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(dll);
        return info;
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        for (int i = 0; i < 8 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpMind.Studio.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static string? FindBuiltDll(string projectDirectory)
    {
        bool release = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
        string[] configurations = release ? ["Release", "Debug"] : ["Debug", "Release"];

        foreach (string configuration in configurations)
        {
            string dll = Path.Combine(
                projectDirectory, "bin", configuration, "net10.0", "SharpMind.Studio.dll");
            if (File.Exists(dll)) return dll;
        }
        return null;
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null) return;
        lock (builder)
        {
            if (builder.Length > 16_384) builder.Remove(0, builder.Length - 12_288);
            builder.AppendLine(line);
        }
    }

    private static string Tail(StringBuilder builder)
    {
        lock (builder)
        {
            string text = builder.ToString().Trim();
            return text.Length <= 2000 ? text : text[^2000..];
        }
    }
}

internal sealed class RemoteStudioUnavailableException(Uri uri)
    : Exception($"SharpMind Studio is not reachable at {uri}.")
{
}

/// <summary>Running compute as reported by the local Studio host.</summary>
public sealed record RunningComputeSummary(
    int Count,
    double HourlyCost,
    double EstimatedCost,
    IReadOnlyList<RunningComputeItem> Instances);

/// <summary>One running instance shown in the exit prompt.</summary>
public sealed record RunningComputeItem(
    string InstanceId,
    string Label,
    string ProviderName,
    string Status,
    string? MainIp,
    double HourlyCost,
    double EstimatedCost,
    double UptimeMinutes,
    bool Orphan)
{
    public string Detail
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(ProviderName)) parts.Add(ProviderName);
            if (!string.IsNullOrWhiteSpace(MainIp)) parts.Add(MainIp);
            parts.Add(Status);
            return string.Join("  ·  ", parts);
        }
    }

    public string CostText => HourlyCost > 0 ? $"${HourlyCost:0.000}/hour" : "cost unknown";

    public string UptimeText
    {
        get
        {
            if (UptimeMinutes < 1) return "just started";
            if (UptimeMinutes < 60) return $"{(int)UptimeMinutes} min";
            if (UptimeMinutes < 1440) return $"{(int)(UptimeMinutes / 60)} h {UptimeMinutes % 60:0} min";
            return $"{(int)(UptimeMinutes / 1440)} d {(int)(UptimeMinutes % 1440 / 60)} h";
        }
    }
}





