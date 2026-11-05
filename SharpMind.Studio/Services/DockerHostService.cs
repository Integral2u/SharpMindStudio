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
        if (!process.Start())
            throw new InvalidOperationException("Docker could not be started.");

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim()
        ?? "Docker returned an error.";
}
