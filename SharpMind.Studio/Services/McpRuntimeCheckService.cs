using System.Diagnostics;
using SharpMind.Studio.Core.Mcp;

namespace SharpMind.Studio.Services;

/// <summary>The result of looking up a local MCP server's launcher.</summary>
public sealed record McpRuntimeStatus(
    bool Found,
    string Command,
    string? Path,
    string? Version,
    string Detail);

/// <summary>
/// Answers the question the add form cannot: will this command actually run?
/// It resolves PATH/PATHEXT and asks known runtimes for their version, but it
/// never executes an arbitrary server executable just to probe it.
/// </summary>
public sealed class McpRuntimeCheckService
{
    private static readonly Dictionary<string, string[]> VersionQueries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["node"] = ["--version"],
            ["npx"] = ["--version"],
            ["npm"] = ["--version"],
            ["uv"] = ["--version"],
            ["uvx"] = ["--version"],
            ["python"] = ["--version"],
            ["python3"] = ["--version"],
            ["dotnet"] = ["--version"],
            ["java"] = ["-version"],
        };

    public async Task<McpRuntimeStatus> CheckAsync(
        McpServerSettings server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.IsHttp)
            return new McpRuntimeStatus(
                true, "http", null, null, "HTTP server: no local runtime is needed.");

        string command = (server.Command ?? "").Trim();
        if (command.Length == 0)
            return new McpRuntimeStatus(false, "", null, null, "Enter a command first.");

        string? path = McpCommandResolver.Resolve(command, server.Environment);
        if (path is null)
            return new McpRuntimeStatus(
                false,
                command,
                null,
                null,
                $"'{command}' was not found in PATH.");

        string? version = await TryVersionAsync(command, server.Environment, cancellationToken)
            .ConfigureAwait(false);

        string detail = string.IsNullOrWhiteSpace(version)
            ? $"Found {command} at {path}."
            : $"{DisplayName(command)} {version} at {path}.";

        return new McpRuntimeStatus(true, command, path, version, detail);
    }

    private static async Task<string?> TryVersionAsync(
        string command,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        string stem = Path.GetFileNameWithoutExtension(command);
        if (!VersionQueries.TryGetValue(stem, out string[]? arguments)) return null;

        try
        {
            ProcessStartInfo startInfo =
                McpCommandResolver.CreateStartInfo(command, arguments, environment);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return null;

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string output = (await stdout.ConfigureAwait(false)).Trim();
            if (output.Length == 0) output = (await stderr.ConfigureAwait(false)).Trim();
            return FirstLine(output);
        }
        catch
        {
            return null;
        }
    }

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string clean = line.Trim();
            if (clean.Length > 0) return clean;
        }
        return "";
    }

    private static string DisplayName(string command) =>
        Path.GetFileNameWithoutExtension(command).ToLowerInvariant() switch
        {
            "node" => "Node.js",
            "npx" or "npm" => "npm",
            "uv" or "uvx" => "uv",
            "dotnet" or "dnx" => ".NET",
            "python" or "python3" => "Python",
            _ => command,
        };
}
