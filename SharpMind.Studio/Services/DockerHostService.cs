using System.ComponentModel;
using System.Diagnostics;
using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed record DockerEngineStatus(bool Available, string Message, string? Version)
{
    public static DockerEngineStatus Missing(string message) => new(false, message, null);
}

public sealed record DockerContainerStatus(
    bool Exists,
    bool Running,
    string? State,
    string? Image,
    string? Ports);

public sealed record DockerStartRequest
{
    public required string ContainerName { get; init; }
    public required string Image { get; init; }
    public required int Port { get; init; }
    public required string Token { get; init; }
    public required string DataFolder { get; init; }
    public bool UseGpu { get; init; }
}

public sealed record DockerPublishRequest
{
    public required string DockerHubUser { get; init; }
    public required string AccessToken { get; init; }
    public required string SourceImage { get; init; }
    public required string TargetImage { get; init; }
}

public sealed record DockerPublishProgress(double? Fraction, string Message);

/// <summary>
/// Runs the local Compute container for the user: no terminal commands, no
/// image tags to memorise. It shells out to the Docker CLI on the Studio host
/// and reports plain-language errors.
/// </summary>
public sealed class DockerHostService
{
    public const string DefaultImage = ComputeProviderPresets.DefaultImage;

    private static readonly string[] DesktopPaths =
    [
        @"C:\Program Files\Docker\Docker\Docker Desktop.exe",
        @"C:\Program Files (x86)\Docker\Docker\Docker Desktop.exe",
    ];

    public bool CanStartDockerDesktop =>
        OperatingSystem.IsWindows() && DesktopPaths.Any(File.Exists);

    public static string DefaultContainerName(string providerId) =>
        ComputeProviderPresets.ContainerName(providerId);

    public static string DefaultDataFolder(string containerName) =>
        Path.Combine(StudioPaths.Compute, containerName);

    public async Task<DockerEngineStatus> GetEngineStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            (int exit, string output, string error) = await RunAsync(
                ["info", "--format", "{{.ServerVersion}}"], cancellationToken);

            if (exit == 0 && !string.IsNullOrWhiteSpace(output))
                return new DockerEngineStatus(true, "Docker engine is running.", output.Trim());

            string message = string.IsNullOrWhiteSpace(error)
                ? "Docker is installed but the engine is not running."
                : FirstLine(error);
            if (message.Contains("pipe", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase)
                || message.Contains("error during connect", StringComparison.OrdinalIgnoreCase))
            {
                message = "Docker Desktop is not running.";
            }

            return DockerEngineStatus.Missing(message);
        }
        catch (Win32Exception)
        {
            return DockerEngineStatus.Missing(
                "Docker Desktop was not found on this PC. Install it, or use a remote host.");
        }
        catch (Exception ex)
        {
            return DockerEngineStatus.Missing(ex.Message);
        }
    }

    public async Task<DockerContainerStatus> GetContainerStatusAsync(
        string containerName,
        CancellationToken cancellationToken = default)
    {
        (int exit, string output, _) = await RunAsync(
            [
                "inspect", "--format",
                "{{.State.Status}}|{{.Config.Image}}|{{json .NetworkSettings.Ports}}",
                containerName,
            ],
            cancellationToken);

        if (exit != 0 || string.IsNullOrWhiteSpace(output))
            return new DockerContainerStatus(false, false, null, null, null);

        string[] parts = output.Trim().Split('|', 3);
        string state = parts.Length > 0 ? parts[0] : "unknown";
        string? image = parts.Length > 1 ? parts[1] : null;
        string? ports = parts.Length > 2 ? parts[2] : null;
        return new DockerContainerStatus(
            true,
            string.Equals(state, "running", StringComparison.OrdinalIgnoreCase),
            state,
            image,
            ports);
    }

    public async Task<DockerContainerStatus> StartAsync(
        DockerStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Port is < 1 or > 65535)
            throw new InvalidOperationException("Choose a port between 1 and 65535.");

        Directory.CreateDirectory(request.DataFolder);

        // Recreate so the token, port and image always match what the panel shows.
        await RunAsync(["rm", "-f", request.ContainerName], cancellationToken);

        var args = new List<string>
        {
            "run", "-d",
            "--name", request.ContainerName,
            "--restart", "unless-stopped",
            "-p", $"{request.Port}:8080",
            "-e", $"SHARPMIND_TOKEN={request.Token}",
            "-e", "SHARPMIND_DATA=/data/SharpMind",
            "-e", "SHARPMIND_MODELS_DIR=/data/SharpMind/Models",
            "-e", "SHARPMIND_PLUGINS=/app/plugins",
            "-v", $"{request.DataFolder}:/data/SharpMind",
        };

        if (request.UseGpu)
            args.AddRange(["--gpus", "all"]);

        args.Add(request.Image);

        (int exit, string output, string error) = await RunAsync(args, cancellationToken);
        if (exit != 0)
        {
            string message = string.IsNullOrWhiteSpace(error) ? output : error;
            if (message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Port {request.Port} is already in use. Choose another port.");
            if (message.Contains("could not select device driver", StringComparison.OrdinalIgnoreCase)
                || message.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The GPU runtime is not available to Docker here. Untick GPU and start it on the CPU.");
            throw new InvalidOperationException(
                $"Docker could not start the container: {FirstLine(message)}");
        }

        // Give the web server a moment to bind before the caller tests it.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return await GetContainerStatusAsync(request.ContainerName, cancellationToken);
    }

    public async Task StopAsync(
        string containerName,
        bool remove,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(["stop", containerName], cancellationToken);
        if (remove)
            await RunAsync(["rm", "-f", containerName], cancellationToken);
    }

    public async Task<string> GetLogsAsync(
        string containerName,
        int tail = 80,
        CancellationToken cancellationToken = default)
    {
        (int exit, string output, string error) = await RunAsync(
            ["logs", "--tail", tail.ToString(), containerName], cancellationToken);
        return exit == 0 ? output.Trim() : FirstLine(error);
    }

    /// <summary>Logs in, tags and pushes an image to Docker Hub. No credentials are stored.</summary>
    public async Task PublishAsync(
        DockerPublishRequest request,
        IProgress<DockerPublishProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DockerHubUser))
            throw new InvalidOperationException("Enter your Docker Hub username.");
        if (string.IsNullOrWhiteSpace(request.AccessToken))
            throw new InvalidOperationException("Enter a Docker Hub access token.");
        if (string.IsNullOrWhiteSpace(request.TargetImage)
            || !request.TargetImage.Contains('/', StringComparison.Ordinal))
            throw new InvalidOperationException("The target image must be <user>/<repository>:<tag>.");

        progress?.Report(new DockerPublishProgress(null, $"Signing in to Docker Hub as {request.DockerHubUser}…"));
        (int loginExit, _, string loginError) = await RunAsync(
            ["login", "-u", request.DockerHubUser, "--password-stdin"],
            cancellationToken,
            stdin: request.AccessToken.Trim());
        if (loginExit != 0)
            throw new InvalidOperationException($"Docker Hub login failed: {FirstLine(loginError)}");

        progress?.Report(new DockerPublishProgress(null, $"Tagging {request.SourceImage}…"));
        (int tagExit, _, string tagError) = await RunAsync(
            ["tag", request.SourceImage, request.TargetImage], cancellationToken);
        if (tagExit != 0)
            throw new InvalidOperationException($"Docker tag failed: {FirstLine(tagError)}");

        progress?.Report(new DockerPublishProgress(0, $"Pushing {request.TargetImage}…"));

        int totalLayers = await GetLayerCountAsync(request.TargetImage, cancellationToken);
        var tracker = new PushTracker(totalLayers);
        string? lastLine = null;

        int pushExit = await RunStreamingAsync(
            ["push", request.TargetImage],
            line =>
            {
                foreach (string part in line.Split(
                    '\r', StringSplitOptions.RemoveEmptyEntries))
                {
                    string clean = System.Text.RegularExpressions.Regex.Replace(
                        part, "\u001b\\[[0-9;?]*[A-Za-z]", "").Trim();
                    if (clean.Length == 0) continue;
                    lastLine = clean;
                    double? fraction = tracker.Update(clean);
                    string message = fraction is { } value
                        ? $"Pushing… {value * 100:0}%"
                        : clean;
                    progress?.Report(new DockerPublishProgress(fraction, message));
                }
            },
            cancellationToken);

        if (pushExit != 0)
        {
            string raw = lastLine ?? "Docker push failed.";
            throw new InvalidOperationException(ExplainPushFailure(raw));
        }

        progress?.Report(new DockerPublishProgress(1, "Image published."));
    }

    private async Task<int> GetLayerCountAsync(string image, CancellationToken cancellationToken)
    {
        (int exit, string output, _) = await RunAsync(
            ["image", "inspect", "--format", "{{len .RootFS.Layers}}", image],
            cancellationToken);
        return exit == 0 && int.TryParse(output.Trim(), out int count) && count > 0 ? count : 0;
    }

    /// <summary>
    /// Tracks Docker's per-layer push lines. When the image's layer count is
    /// known, progress is completed layers plus the current layer's byte
    /// fraction; that works even when a re-push only reports "Pushed".
    /// </summary>
    private sealed class PushTracker(int totalLayers)
    {
        private readonly HashSet<string> _done = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (double Current, double Total)> _bytes =
            new(StringComparer.Ordinal);
        private string? _currentKey;
        private double _currentFraction;

        public double? Update(string line)
        {
            int colon = line.IndexOf(':');
            string key = colon > 0 ? line[..colon].Trim() : line.Trim();
            string payload = colon > 0 ? line[(colon + 1)..] : line;
            if (key.Length == 0 || key.Length > 80) return Fraction();

            if (payload.Contains("Layer already exists", StringComparison.OrdinalIgnoreCase)
                || payload.Contains("Pushed", StringComparison.OrdinalIgnoreCase)
                   && !payload.Contains("Pushing", StringComparison.OrdinalIgnoreCase))
            {
                _done.Add(key);
                if (string.Equals(_currentKey, key, StringComparison.Ordinal)) _currentKey = null;
                return Fraction();
            }

            int slash = payload.IndexOf('/');
            if (slash > 0 && payload.Contains("Pushing", StringComparison.OrdinalIgnoreCase))
            {
                double? current = ParseBytes(payload[..slash]);
                double? total = ParseBytes(payload[(slash + 1)..]);
                if (current is { } c && total is { } t && t > 0)
                {
                    _bytes[key] = (Math.Min(c, t), t);
                    _currentKey = key;
                    _currentFraction = Math.Clamp(c / t, 0, 1);
                    return Fraction();
                }
            }

            int percent = payload.IndexOf('%');
            if (percent > 0)
            {
                int start = percent - 1;
                while (start >= 0 && (char.IsDigit(payload[start]) || payload[start] == '.')) start--;
                if (double.TryParse(payload[(start + 1)..percent], out double value)
                    && value is >= 0 and <= 100)
                {
                    return Math.Clamp(value / 100.0, 0, 0.999);
                }
            }

            return Fraction();
        }

        private double? Fraction()
        {
            if (totalLayers > 0)
            {
                double partial = _currentKey is not null ? _currentFraction : 0;
                return Math.Clamp((_done.Count + partial) / totalLayers, 0, 0.999);
            }

            if (_bytes.Count == 0) return null;
            double current = _bytes.Values.Sum(layer => layer.Current);
            double total = _bytes.Values.Sum(layer => layer.Total);
            return total <= 0 ? null : Math.Clamp(current / total, 0, 0.999);
        }

        private static double? ParseBytes(string text)
        {
            string value = text.Trim();
            int index = 0;
            while (index < value.Length
                   && (char.IsDigit(value[index]) || value[index] == '.' || value[index] == ','))
            {
                index++;
            }

            if (index == 0) return null;
            string number = value[..index].Replace(',', '.');
            if (!double.TryParse(
                    number,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double size))
            {
                return null;
            }

            string unit = value[index..].Trim().ToUpperInvariant();
            double multiplier = unit switch
            {
                "B" => 1,
                "KB" or "KIB" => 1024,
                "MB" or "MIB" => 1024 * 1024,
                "GB" or "GIB" => 1024L * 1024 * 1024,
                _ => 0,
            };
            return multiplier == 0 ? null : size * multiplier;
        }
    }

    private static string ExplainPushFailure(string raw)
    {
        string text = raw.Trim();
        if (text.Contains("proxyconnect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("192.168.65.1", StringComparison.Ordinal)
            || text.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "Docker Desktop's HTTP proxy timed out during the upload. Open Docker Desktop → "
                + "Settings → Resources → Proxies and turn off 'Use system proxy' (or fix the proxy), "
                + "then press Push image again. Layers already uploaded are reused. If a VPN or "
                + "security tool is intercepting traffic, disconnect it for the push.";
        }

        return text;
    }

    public void StartDockerDesktop()
    {
        string? path = DesktopPaths.FirstOrDefault(File.Exists);
        if (path is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // The caller re-checks the engine and shows the real error.
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? stdin = null)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("Docker could not be started.");

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }

    /// <summary>Runs Docker and forwards each output line as it arrives (push progress).</summary>
    private static async Task<int> RunStreamingAsync(
        IReadOnlyList<string> arguments,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Docker could not be started.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit(); // flush the asynchronous readers
        return process.ExitCode;
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim()
        ?? "Docker returned an error.";
}
