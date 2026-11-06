using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SharpMind.Studio.Core.Compute;

/// <summary>
/// Connects to a Compute host over SSH and performs the one-time setup a cloud
/// dashboard normally asks you to paste into a terminal: install Docker (and the
/// NVIDIA container toolkit on GPU hosts), pull the image, and start the
/// combined Studio + Compute container. Secrets are supplied by the caller and
/// never written to the remote command line in plain text beyond the docker
/// environment the container itself needs.
/// </summary>
public sealed class SshHostService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(60);

    public async Task<SshProbe> ProbeAsync(
        SshCredentials credentials,
        IProgress<SshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        using SshClient client = await ConnectAsync(credentials, cancellationToken).ConfigureAwait(false);
        Report(progress, "connect", $"Connected to {credentials.Target.Display}.");
        SshCommandResult result = await RunAsync(
            client,
            SshSetupScript.Probe(),
            "probe",
            progress,
            cancellationToken).ConfigureAwait(false);
        return SshProbe.Parse(result.Output);
    }

    public async Task<SshSetupResult> SetupAsync(
        SshCredentials credentials,
        SshSetupRequest request,
        IProgress<SshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var warnings = new List<string>();
        using SshClient client = await ConnectAsync(credentials, cancellationToken).ConfigureAwait(false);
        Report(progress, "connect", $"Connected to {credentials.Target.Display}.");

        SshProbe probe = await ProbeOnAsync(client, progress, cancellationToken).ConfigureAwait(false);
        Report(progress, "probe", probe.Summary);

        if (!probe.HasDocker)
        {
            Report(progress, "docker", "Docker is not installed. Installing it now…");
            await RunAsync(
                client,
                SshSetupScript.InstallDocker(),
                "docker",
                progress,
                cancellationToken).ConfigureAwait(false);
            probe = await ProbeOnAsync(client, progress, cancellationToken).ConfigureAwait(false);
            if (!probe.HasDocker)
            {
                throw new InvalidOperationException(
                    "Docker was installed but is not on the PATH yet. Reconnect and try again.");
            }
        }

        if (request.UseGpu)
        {
            if (!probe.HasGpu)
            {
                throw new InvalidOperationException(
                    $"No NVIDIA driver was found on {credentials.Target.Host} (nvidia-smi is missing). "
                    + "Use a GPU image from the provider, or install the NVIDIA driver and reboot, "
                    + "then run setup again. To run on the CPU instead, turn off 'GPU host' in Edit.");
            }

            if (!probe.HasNvidiaToolkit)
            {
                Report(progress, "toolkit", "Installing the NVIDIA container toolkit…");
                await RunAsync(
                    client,
                    SshSetupScript.InstallNvidiaToolkit(),
                    "toolkit",
                    progress,
                    cancellationToken).ConfigureAwait(false);
                probe = await ProbeOnAsync(client, progress, cancellationToken).ConfigureAwait(false);
                if (!probe.HasNvidiaToolkit)
                {
                    throw new InvalidOperationException(
                        "The NVIDIA container toolkit is not visible after installation. "
                        + "Check that the host's package manager completed, then try again.");
                }
            }
        }
        else
        {
            warnings.Add("Started without --gpus all (CPU host). GPU jobs fall back to the CPU.");
        }

        if (TryParseSizeGb(probe.DiskFree) is { } freeGb && freeGb < 5)
        {
            string warning = $"Only {probe.DiskFree} is free on the host root disk; the image needs about 2 GB.";
            warnings.Add(warning);
            Report(progress, "pull", warning);
        }

        if (SshSetupScript.LooksUnpublished(request.Image))
        {
            throw new InvalidOperationException(
                $"The image '{request.Image}' has no registry prefix, so the host cannot pull it. "
                + "Press Edit and enter the full name (for example your-name/sharpmind-server:gpu), "
                + "or publish the image from this panel first.");
        }

        Report(progress, "pull", $"Pulling {request.Image}. The first pull can take a few minutes…");
        await RunAsync(
            client,
            SshSetupScript.Pull(request.Image),
            "pull",
            progress,
            cancellationToken).ConfigureAwait(false);

        Report(progress, "run", "Starting the SharpMind Server container…");
        await RunAsync(
            client,
            SshSetupScript.Run(request),
            "run",
            progress,
            cancellationToken).ConfigureAwait(false);

        SshCommandResult inspect = await RunAsync(
            client,
            SshSetupScript.Inspect(request.ContainerName),
            "run",
            progress,
            cancellationToken).ConfigureAwait(false);
        string status = inspect.Output.Trim();
        if (status.Length == 0 || status.Equals("missing", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The container '{request.ContainerName}' did not stay up. Press Container logs for the reason.");
        }

        Report(progress, "ready", $"Container {request.ContainerName}: {status}");
        return new SshSetupResult(probe, request.ContainerName, status, warnings);
    }

    public async Task<string> LogsAsync(
        SshCredentials credentials,
        string containerName,
        int tail = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        using SshClient client = await ConnectAsync(credentials, cancellationToken).ConfigureAwait(false);
        SshCommandResult result = await RunAsync(
            client,
            SshSetupScript.Logs(containerName, tail),
            "logs",
            null,
            cancellationToken).ConfigureAwait(false);
        return result.Output.Trim();
    }

    public async Task<string> StopAsync(
        SshCredentials credentials,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        using SshClient client = await ConnectAsync(credentials, cancellationToken).ConfigureAwait(false);
        SshCommandResult result = await RunAsync(
            client,
            SshSetupScript.Stop(containerName),
            "stop",
            null,
            cancellationToken).ConfigureAwait(false);
        return result.Output.Trim();
    }

    public async Task<string> InspectAsync(
        SshCredentials credentials,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        using SshClient client = await ConnectAsync(credentials, cancellationToken).ConfigureAwait(false);
        SshCommandResult result = await RunAsync(
            client,
            SshSetupScript.Inspect(containerName),
            "inspect",
            null,
            cancellationToken).ConfigureAwait(false);
        return result.Output.Trim();
    }

    private static async Task<SshProbe> ProbeOnAsync(
        SshClient client,
        IProgress<SshProgress>? progress,
        CancellationToken cancellationToken)
    {
        SshCommandResult result = await RunAsync(
            client,
            SshSetupScript.Probe(),
            "probe",
            progress,
            cancellationToken).ConfigureAwait(false);
        return SshProbe.Parse(result.Output);
    }

    private static void Validate(SshSetupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Image))
            throw new InvalidOperationException("Enter the server image to deploy.");
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new InvalidOperationException(
                "The Compute token is empty. Save a token for this host before deploying.");
        if (!IsValidContainerName(request.ContainerName))
            throw new InvalidOperationException(
                "The container name may only contain letters, digits, '.', '_' and '-'.");
        if (string.IsNullOrWhiteSpace(request.DataFolder)
            || !request.DataFolder.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException("The data folder must be an absolute Linux path.");
    }

    private static bool IsValidContainerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!char.IsLetterOrDigit(name[0])) return false;
        return name.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');
    }

    private static void Report(IProgress<SshProgress>? progress, string stage, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        progress?.Report(new SshProgress(stage, SshSetupScript.FormatLogLine(line)));
    }

    private static async Task<SshClient> ConnectAsync(
        SshCredentials credentials,
        CancellationToken cancellationToken)
    {
        SshTarget target = credentials.Target;
        var keyFiles = new List<PrivateKeyFile>();
        try
        {
            var methods = new List<AuthenticationMethod>();

            if (credentials.HasKey)
            {
                PrivateKeyFile key = LoadKey(credentials.PrivateKeyPath!, credentials.PrivateKeyPassphrase);
                keyFiles.Add(key);
                methods.Add(new PrivateKeyAuthenticationMethod(target.User, key));
            }

            if (credentials.HasPassword)
            {
                methods.Add(new PasswordAuthenticationMethod(target.User, credentials.Password!));
            }

            if (methods.Count == 0)
            {
                foreach (string path in DefaultKeyPaths())
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        PrivateKeyFile key = LoadKey(path, null);
                        keyFiles.Add(key);
                        methods.Add(new PrivateKeyAuthenticationMethod(target.User, key));
                        break;
                    }
                    catch
                    {
                        // Try the next default key; a broken optional key must not block a password.
                    }
                }
            }

            if (methods.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No SSH key or password was given, and no default key was found in {Path.Combine(HomeFolder(), ".ssh")}. "
                    + "Press Edit and enter a private key path, or the host password.");
            }

            var info = new ConnectionInfo(target.Host, target.Port, target.User, [.. methods])
            {
                Timeout = ConnectTimeout,
            };
            var client = new SshClient(info);
            // Trust on first use: the key is accepted but not pinned. The host is
            // reached over the provider's network and the user chose the address.
            client.HostKeyReceived += (_, e) => e.CanTrust = true;

            try
            {
                await Task.Run(client.Connect, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            return client;
        }
        catch (SshAuthenticationException ex)
        {
            throw new InvalidOperationException(
                $"SSH login failed for {target.User}@{target.Host}: {ex.Message} "
                + "Check the user and the key/password.", ex);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Could not reach {target.Host}:{target.Port} over SSH: {ex.Message} "
                + "Check the address, the provider firewall, and that the instance is running.", ex);
        }
        catch (SshConnectionException ex)
        {
            throw new InvalidOperationException(
                $"Could not reach {target.Host}:{target.Port} over SSH: {ex.Message} "
                + "Check the provider firewall (TCP 22) and that the instance is running.", ex);
        }
        catch (SshException ex)
        {
            throw new InvalidOperationException($"SSH to {target.Display} failed: {ex.Message}", ex);
        }
        finally
        {
            foreach (PrivateKeyFile key in keyFiles) key.Dispose();
        }
    }

    private static PrivateKeyFile LoadKey(string path, string? passphrase)
    {
        string expanded = ExpandHome(path);
        if (!File.Exists(expanded))
            throw new InvalidOperationException($"The private key file was not found: {expanded}");
        try
        {
            return new PrivateKeyFile(expanded, passphrase);
        }
        catch (Exception ex) when (ex is SshException or NotSupportedException or InvalidOperationException)
        {
            string hint = ex.Message.Contains("check bytes", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                    ? "The passphrase for this key looks incorrect."
                    : "Use an OpenSSH or PEM key, or paste a password instead.";
            throw new InvalidOperationException(
                $"Could not read the private key {expanded}: {ex.Message} {hint}", ex);
        }
    }

    private static string ExpandHome(string path)
    {
        string value = path.Trim();
        if (value == "~") return HomeFolder();
        if (value.StartsWith("~/", StringComparison.Ordinal)
            || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(HomeFolder(), value[2..]);
        }

        return value;
    }

    private static string HomeFolder() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IEnumerable<string> DefaultKeyPaths()
    {
        string ssh = Path.Combine(HomeFolder(), ".ssh");
        yield return Path.Combine(ssh, "id_ed25519");
        yield return Path.Combine(ssh, "id_rsa");
        yield return Path.Combine(ssh, "id_ecdsa");
    }

    private static async Task<SshCommandResult> RunAsync(
        SshClient client,
        string script,
        string stage,
        IProgress<SshProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Base64 keeps the script out of the shell's quoting rules entirely.
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        using SshCommand command = client.CreateCommand($"echo {encoded} | base64 -d | bash");
        command.CommandTimeout = CommandTimeout;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var stopPumps = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task execute;
        try
        {
            execute = command.ExecuteAsync(cancellationToken);
        }
        catch (SshException ex)
        {
            throw new InvalidOperationException($"SSH command failed to start: {ex.Message}", ex);
        }

        Task outputPump = PumpAsync(command.OutputStream, stdout, stage, progress, stopPumps.Token);
        Task errorPump = PumpAsync(command.ExtendedOutputStream, stderr, stage, progress, stopPumps.Token);

        try
        {
            await execute.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SshOperationTimeoutException ex)
        {
            throw new InvalidOperationException(
                $"The remote command timed out after {CommandTimeout.TotalMinutes:0} minutes.", ex);
        }
        finally
        {
            stopPumps.Cancel();
            try
            {
                await Task.WhenAll(outputPump, errorPump)
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The command is done; a stalled reader is not worth failing the run.
            }
        }

        string output = stdout.ToString().TrimEnd();
        string error = stderr.ToString().TrimEnd();
        int exit = command.ExitStatus ?? 0;
        if (exit != 0)
        {
            string detail = FailureDetail(output, error);
            if (detail.Length == 0) detail = "no output";
            throw new InvalidOperationException(
                $"Remote command failed (exit {exit}): {detail}");
        }

        return new SshCommandResult(exit, output, error);
    }

    private static async Task PumpAsync(
        Stream stream,
        StringBuilder sink,
        string stage,
        IProgress<SshProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var line = new StringBuilder();
        var buffer = new char[4096];

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char value = buffer[i];
                    if (value is '\r' or '\n')
                    {
                        Emit();
                        continue;
                    }

                    if (line.Length < 8000) line.Append(value);
                }
            }

            if (line.Length > 0) Emit();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }

        void Emit()
        {
            string text = SshSetupScript.FormatLogLine(line.ToString());
            line.Clear();
            if (text.Length == 0) return;
            sink.AppendLine(text);
            progress?.Report(new SshProgress(stage, text));
        }
    }

    private static string LastLines(string value, int count)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string[] lines = value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(count));
    }

    /// <summary>
    /// Picks the useful end of a failed command: the last line that looks like
    /// an error, plus its neighbour, rather than a trailing progress line such as
    /// "sleep 20" that hides the actual reason.
    /// </summary>
    private static string FailureDetail(string output, string error)
    {
        string combined = string.Join(
            '\n',
            new[] { error, output }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (combined.Length == 0) return "";

        string[] lines = combined.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int index = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (LooksLikeFailure(lines[i]))
            {
                index = i;
                break;
            }
        }

        if (index < 0) return LastLines(combined, 6);

        int start = Math.Max(0, index - 1);
        return string.Join(" | ", lines.Skip(start).Take(4));
    }

    private static bool LooksLikeFailure(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("denied", StringComparison.OrdinalIgnoreCase)
        || line.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
        || line.Contains("not supported", StringComparison.OrdinalIgnoreCase)
        || line.Contains("cannot", StringComparison.OrdinalIgnoreCase)
        || line.Contains("no such", StringComparison.OrdinalIgnoreCase)
        || line.Contains("permission", StringComparison.OrdinalIgnoreCase);

    private static double? TryParseSizeGb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim().ToUpperInvariant();
        double multiplier = text.EndsWith("T", StringComparison.Ordinal) ? 1024
            : text.EndsWith("G", StringComparison.Ordinal) ? 1
            : text.EndsWith("M", StringComparison.Ordinal) ? 1.0 / 1024
            : 0;
        if (multiplier == 0) return null;
        string number = text[..^1];
        return double.TryParse(
            number,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double parsed)
            ? parsed * multiplier
            : null;
    }

    private readonly record struct SshCommandResult(int ExitCode, string Output, string Error);
}
