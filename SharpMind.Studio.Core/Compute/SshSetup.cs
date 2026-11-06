using System.Globalization;

namespace SharpMind.Studio.Core.Compute;

/// <summary>Where the SSH helper connects: the host's public address and login user.</summary>
public sealed record SshTarget(string Host, int Port, string User)
{
    public string Display => $"{User}@{Host}:{Port}";
}

/// <summary>
/// Resolves the SSH endpoint from a Compute host. The SSH host can differ from
/// the API address (for example a public DNS name while the API URL uses an IP),
/// so it is stored separately and only falls back to the URL's host when blank.
/// Loopback URLs are never used: an SSH session to 127.0.0.1 is this PC.
/// </summary>
public static class SshTargets
{
    public const int DefaultPort = 22;
    public const string DefaultUser = "root";

    public static SshTarget? FromSettings(ComputeProviderSettings provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        string host = provider.SshHost?.Trim() ?? "";
        if (host.Length == 0
            && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out Uri? uri)
            && !IsLoopback(uri.Host))
        {
            host = uri.Host;
        }

        if (host.Length == 0) return null;

        int port = provider.SshPort is > 0 and <= 65535
            ? provider.SshPort.Value
            : DefaultPort;
        string user = string.IsNullOrWhiteSpace(provider.SshUser)
            ? DefaultUser
            : provider.SshUser.Trim();
        return new SshTarget(host, port, user);
    }

    public static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        string value = host.Trim();
        return value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value == "0.0.0.0"
            || value == "::1"
            || value.StartsWith("127.", StringComparison.Ordinal);
    }
}

/// <summary>Decrypted SSH login material. Never written back to disk as-is.</summary>
public sealed record SshCredentials(
    SshTarget Target,
    string? PrivateKeyPath,
    string? PrivateKeyPassphrase,
    string? Password)
{
    public bool HasKey => !string.IsNullOrWhiteSpace(PrivateKeyPath);
    public bool HasPassword => !string.IsNullOrWhiteSpace(Password);
    public string AuthSummary => HasKey ? "private key" : HasPassword ? "password" : "default key";
}

/// <summary>The values setup needs to pull and run the combined server image.</summary>
public sealed record SshSetupRequest
{
    public required string Image { get; init; }
    public required string Token { get; init; }
    public string ContainerName { get; init; } = "sharpmind";
    public bool UseGpu { get; init; }
    public int ApiPort { get; init; } = ComputeProviderPresets.DefaultPort;
    public int StudioPort { get; init; } = 5117;
    public string DataFolder { get; init; } = "/data/SharpMind";
}

/// <summary>What the helper found on the host. Empty strings mean "not installed".</summary>
public sealed record SshProbe(
    string? Os,
    string? Architecture,
    string? DockerVersion,
    string? Gpu,
    bool HasNvidiaToolkit,
    string? DiskFree)
{
    public bool HasDocker => !string.IsNullOrWhiteSpace(DockerVersion);
    public bool HasGpu => !string.IsNullOrWhiteSpace(Gpu);

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Os)) parts.Add(Os.Trim());
            if (!string.IsNullOrWhiteSpace(Architecture)) parts.Add(Architecture.Trim());
            if (HasDocker) parts.Add($"Docker {DockerVersion!.Trim()}");
            else parts.Add("Docker not installed");
            parts.Add(HasGpu ? Gpu!.Trim() : "no NVIDIA GPU driver");
            if (!string.IsNullOrWhiteSpace(DiskFree)) parts.Add($"{DiskFree!.Trim()} free");
            return string.Join(" · ", parts);
        }
    }

    public static SshProbe Parse(string? output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in (output ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (!line.StartsWith("SM_", StringComparison.Ordinal)) continue;
            int equals = line.IndexOf('=');
            if (equals <= 3) continue;
            values[line[..equals]] = line[(equals + 1)..].Trim();
        }

        return new SshProbe(
            Get("SM_OS"),
            Get("SM_ARCH"),
            Get("SM_DOCKER"),
            Get("SM_GPU"),
            string.Equals(Get("SM_TOOLKIT"), "yes", StringComparison.OrdinalIgnoreCase),
            Get("SM_DISK"));

        string? Get(string name) =>
            values.TryGetValue(name, out string? value) && value.Length > 0 ? value : null;
    }
}

/// <summary>One line of setup progress. <paramref name="Stage"/> groups the output.</summary>
public sealed record SshProgress(string Stage, string Line);

/// <summary>What setup left behind on the host.</summary>
public sealed record SshSetupResult(
    SshProbe Probe,
    string ContainerName,
    string ContainerStatus,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The remote shell snippets used by the SSH helper. Kept as plain bash in Core
/// so they can be reviewed and unit-tested without a live host. Everything that
/// comes from settings is single-quoted, so spaces, quotes and apostrophes in a
/// token or path cannot break out of the script.
/// </summary>
public static class SshSetupScript
{
    public static string Quote(string value) =>
        "'" + (value ?? "").Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Prints SM_* key=value lines that <see cref="SshProbe.Parse"/> reads.</summary>
    public static string Probe() => """
        set -u
        echo "SM_OS=$(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME" || uname -s)"
        echo "SM_ARCH=$(uname -m)"
        if command -v docker >/dev/null 2>&1; then
          echo "SM_DOCKER=$(docker --version 2>/dev/null | sed 's/^Docker version //; s/,.*//')"
        else
          echo "SM_DOCKER="
        fi
        if command -v nvidia-smi >/dev/null 2>&1; then
          echo "SM_GPU=$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -n 1)"
        else
          echo "SM_GPU="
        fi
        if dpkg -s nvidia-container-toolkit >/dev/null 2>&1 && command -v nvidia-ctk >/dev/null 2>&1; then
          echo "SM_TOOLKIT=yes"
        else
          echo "SM_TOOLKIT=no"
        fi
        echo "SM_DISK=$(df -h / 2>/dev/null | awk 'NR==2 {print $4}')"
        """;

    public static string InstallDocker() => """
        set -euo pipefail
        export DEBIAN_FRONTEND=noninteractive
        if command -v apt-get >/dev/null 2>&1 && ! command -v curl >/dev/null 2>&1; then
          apt-get update -y
          apt-get install -y curl ca-certificates
        fi
        echo "Installing Docker from get.docker.com…"
        curl -fsSL https://get.docker.com | sh
        systemctl enable --now docker
        docker --version
        """;

    public static string InstallNvidiaToolkit() => """
        set -euo pipefail
        export DEBIAN_FRONTEND=noninteractive
        if ! command -v curl >/dev/null 2>&1 || ! command -v gpg >/dev/null 2>&1; then
          apt-get update -y
          apt-get install -y curl ca-certificates gnupg
        fi
        install -m 0755 -d /usr/share/keyrings
        curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
          | gpg --batch --yes --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
        curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
          | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
          > /etc/apt/sources.list.d/nvidia-container-toolkit.list
        apt-get update -y
        apt-get install -y nvidia-container-toolkit
        nvidia-ctk runtime configure --runtime=docker
        systemctl restart docker
        nvidia-ctk --version
        """;

    public static string Pull(string image) => """
        set -euo pipefail
        docker pull __IMAGE__
        """.Replace("__IMAGE__", Quote(image), StringComparison.Ordinal);

    public static string Run(SshSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ApiPort is < 1 or > 65535)
            throw new InvalidOperationException("The API port must be between 1 and 65535.");
        if (request.StudioPort is < 1 or > 65535)
            throw new InvalidOperationException("The Studio port must be between 1 and 65535.");

        string script = """
            set -euo pipefail
            IMAGE=__IMAGE__
            CONTAINER=__CONTAINER__
            TOKEN=__TOKEN__
            API_PORT=__API_PORT__
            STUDIO_PORT=__STUDIO_PORT__
            DATA=__DATA__

            if docker ps --format '{{.Names}} {{.Ports}}' \
                 | grep -E ':(8080|5117)->' \
                 | grep -v "^${CONTAINER} " >/dev/null 2>&1; then
              echo "Port 8080 or 5117 is already used on this host:"
              docker ps --format '{{.Names}} {{.Ports}}' | grep -E ':(8080|5117)->' | grep -v "^${CONTAINER} " || true
              echo "Remove that container, or change the ports, before deploying."
              exit 4
            fi

            docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
            docker run -d --restart unless-stopped \
              --name "$CONTAINER" \
              -p "${API_PORT}:8080" \
              -p "${STUDIO_PORT}:5117" \
              -e "SHARPMIND_TOKEN=$TOKEN" \
              -e "SHARPMIND_DATA=$DATA" \
              -e "SHARPMIND_MODELS_DIR=$DATA/Models" \
              -e "SHARPMIND_PLUGINS=/app/plugins" \
              -v "${DATA}:${DATA}" \
              __GPU__ \
              "$IMAGE"

            docker ps --filter "name=^${CONTAINER}$" --format '{{.Status}}|{{.Ports}}'
            """;

        return script
            .Replace("__IMAGE__", Quote(request.Image), StringComparison.Ordinal)
            .Replace("__CONTAINER__", Quote(request.ContainerName), StringComparison.Ordinal)
            .Replace("__TOKEN__", Quote(request.Token), StringComparison.Ordinal)
            .Replace(
                "__API_PORT__",
                Quote(request.ApiPort.ToString(CultureInfo.InvariantCulture)),
                StringComparison.Ordinal)
            .Replace(
                "__STUDIO_PORT__",
                Quote(request.StudioPort.ToString(CultureInfo.InvariantCulture)),
                StringComparison.Ordinal)
            .Replace("__DATA__", Quote(request.DataFolder), StringComparison.Ordinal)
            .Replace("__GPU__", request.UseGpu ? "--gpus all" : "", StringComparison.Ordinal);
    }

    public static string Inspect(string container) => """
        set -u
        NAME=__CONTAINER__
        if docker ps -a --filter "name=^${NAME}$" --format '{{.State.Status}}|{{.Image}}|{{.Ports}}' \
             | head -n 1 | grep -q .; then
          docker ps -a --filter "name=^${NAME}$" --format '{{.State.Status}}|{{.Image}}|{{.Ports}}' | head -n 1
        else
          echo "missing"
        fi
        """.Replace("__CONTAINER__", Quote(container), StringComparison.Ordinal);

    public static string Logs(string container, int tail = 100) => """
        set -u
        docker logs --tail __TAIL__ __CONTAINER__ 2>&1 | sed 's/\r$//'
        """
        .Replace("__TAIL__", Math.Clamp(tail, 1, 5000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("__CONTAINER__", Quote(container), StringComparison.Ordinal);

    public static string Stop(string container) => """
        set -u
        NAME=__CONTAINER__
        docker stop "$NAME" >/dev/null 2>&1 || true
        STATUS=$(docker ps -a --filter "name=^${NAME}$" --format '{{.Status}}' | head -n 1)
        echo "${STATUS:-missing}"
        """.Replace("__CONTAINER__", Quote(container), StringComparison.Ordinal);

    /// <summary>Picks the image to deploy when a host still carries the local-only default.</summary>
    public static string ResolveImage(ComputeProviderSettings provider, string? registryImage)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string configured = provider.DockerImage?.Trim() ?? "";
        string? published = registryImage?.Trim();
        if (configured.Length == 0
            || string.Equals(configured, ComputeProviderPresets.DefaultImage, StringComparison.OrdinalIgnoreCase))
        {
            configured = !string.IsNullOrWhiteSpace(published)
                ? published!
                : ComputeProviderPresets.DefaultServerImage;
        }

        return configured;
    }

    /// <summary>True when the image name looks like it was never pushed to a registry.</summary>
    public static bool LooksUnpublished(string image)
    {
        string value = image.Trim();
        if (value.Length == 0) return true;
        string name = value.Split(':', 2)[0];
        return !name.Contains('/', StringComparison.Ordinal)
            || name.StartsWith("sharpmind-compute", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("sharpmind-server", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A compact, single-line summary for the host card. Never includes secrets.</summary>
    public static string Describe(ComputeProviderSettings provider, string? registryImage = null)
    {
        SshTarget? target = SshTargets.FromSettings(provider);
        if (target is null) return "SSH not configured. Press Edit and add a host, user and key or password.";

        string auth = !string.IsNullOrWhiteSpace(provider.SshKeyPath)
            ? "private key"
            : provider.SshPasswordProtected is { Length: > 0 }
                ? "password"
                : "default key";
        string image = ResolveImage(provider, registryImage);
        string gpu = provider.UseGpu ? "GPU" : "CPU";
        return $"{target.Display} · {auth} · {gpu} · {image}";
    }

    public static string FormatLogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        string clean = line.TrimEnd();
        return clean.Length <= 2000 ? clean : clean[..2000] + "…";
    }
}
