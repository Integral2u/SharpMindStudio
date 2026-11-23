using System.Diagnostics;
using System.Text;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Work;

public sealed record WorkCommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    IReadOnlyList<string> SecretValues,
    bool TimedOut,
    string? Error)
{
    public static WorkCommandResult Failed(string error) =>
        new(-1, "", "", [], false, error);

    public static WorkCommandResult From(
        int exitCode,
        string stdout,
        string stderr,
        IReadOnlyList<string> secrets,
        bool timedOut) =>
        new(exitCode, stdout, stderr, secrets, timedOut, null);
}

/// <summary>
/// Runs one already-allowed executable with argv, maps the project permission
/// and expands/redacts Vault references. A null policy is Unrestricted mode;
/// a non-null empty policy refuses every external command. It never uses a shell.
/// </summary>
public sealed class WorkCommandRunner(
    CommandPolicy? policy,
    WorkPathPolicy paths,
    Func<WorkToolAccess, Task<bool>> permission,
    ISecretResolver? secrets = null,
    Func<string>? currentDirectory = null)
{
    private const int MaxCaptured = 2 * 1024 * 1024;

    private readonly ISecretResolver _secrets = secrets ?? NullSecretResolver.Instance;

    public CommandPolicy? Policy => policy;

    public async Task<WorkCommandResult> RunAsync(
        string toolName,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        string? stdin = null,
        int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        AllowedCommandMatch? match = null;
        if (policy is not null)
        {
            if (policy.IsEmpty)
            {
                return WorkCommandResult.Failed(
                    "No commands are allowed for this project. Add a command to the allowlist "
                    + "or switch Command access to Unrestricted.");
            }

            match = policy.Resolve(executable);
            if (match is null)
            {
                return WorkCommandResult.Failed(
                    $"Command '{executable}' is not in the allowed list. Allowed: {policy.AllowedNames}.");
            }
        }

        string cwd;
        try
        {
            string baseDirectory = currentDirectory?.Invoke() ?? paths.Root;
            string target = string.IsNullOrWhiteSpace(workingDirectory) || workingDirectory == "."
                ? baseDirectory
                : workingDirectory;
            cwd = paths.ResolveDirectory(target, baseDirectory);
        }
        catch (Exception ex)
        {
            return WorkCommandResult.Failed(ex.Message);
        }

        var used = new List<SecretUse>();
        var missing = new List<string>();
        var expandedArguments = new List<string>();

        foreach (string prefix in match?.PrefixArguments ?? [])
        {
            SecretExpansion expansion = SecretTemplate.Expand(prefix, _secrets.Resolve);
            Collect(expansion, used, missing);
            expandedArguments.Add(expansion.Text);
        }

        foreach (string argument in arguments)
        {
            SecretExpansion expansion = SecretTemplate.Expand(argument, _secrets.Resolve);
            Collect(expansion, used, missing);
            expandedArguments.Add(expansion.Text);
        }

        var childEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
                childEnvironment[key] = value;
        }

        foreach ((string key, string value) in childEnvironment.ToArray())
        {
            SecretExpansion expansion = SecretTemplate.Expand(value, _secrets.Resolve);
            Collect(expansion, used, missing);
            childEnvironment[key] = expansion.Text;
        }

        if (missing.Count > 0)
            return WorkCommandResult.Failed(new SecretMissingException(missing).Message);

        string[] secretValues = [.. used
            .Select(use => use.Value)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)];

        string displayName = match?.Command.Name
            ?? Path.GetFileNameWithoutExtension(executable);
        if (displayName.Length == 0) displayName = executable;
        string detail = $"{displayName} "
            + string.Join(" ", (match?.PrefixArguments ?? []).Concat(arguments));
        detail = detail.Trim();
        if (used.Count > 0)
        {
            detail += $" (uses secret: {string.Join(", ", used.Select(use => use.Name).Distinct(StringComparer.OrdinalIgnoreCase))})";
        }

        var access = new WorkToolAccess(toolName, WorkToolCategory.Execute, cwd)
        {
            Detail = detail,
        };
        if (!await permission(access).ConfigureAwait(false))
            return WorkCommandResult.Failed("Permission denied for this command.");

        ProcessStartInfo startInfo;
        try
        {
            startInfo = McpCommandResolver.CreateStartInfo(
                match?.Executable ?? executable,
                expandedArguments,
                childEnvironment);
            startInfo.WorkingDirectory = cwd;
        }
        catch (Exception ex)
        {
            return WorkCommandResult.Failed(
                SecretTemplate.Redact($"Could not prepare {executable}: {ex.Message}", secretValues));
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return WorkCommandResult.Failed($"Could not start {executable}.");
        }
        catch (Exception ex)
        {
            return WorkCommandResult.Failed(
                SecretTemplate.Redact(
                    $"Could not start {executable}: {ex.GetBaseException().Message}",
                    secretValues));
        }

        try
        {
            if (!string.IsNullOrEmpty(stdin))
                await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch
        {
            // A command may exit before reading stdin; the exit code still matters.
        }

        Task<CapturedText> stdout = ReadCappedAsync(process.StandardOutput);
        Task<CapturedText> stderr = ReadCappedAsync(process.StandardError);
        bool timedOut = false;
        int timeout = Math.Clamp(timeoutSeconds, 1, 600);

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
        }

        CapturedText outText = await stdout.ConfigureAwait(false);
        CapturedText errText = await stderr.ConfigureAwait(false);
        int exitCode = timedOut ? -1 : process.ExitCode;

        return WorkCommandResult.From(
            exitCode,
            outText.Text,
            errText.Text,
            secretValues,
            timedOut);
    }

    private static void Collect(
        SecretExpansion expansion,
        List<SecretUse> used,
        List<string> missing)
    {
        used.AddRange(expansion.Used);
        missing.AddRange(expansion.Missing);
    }

    private static async Task<CapturedText> ReadCappedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        bool truncated = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;

            if (text.Length < MaxCaptured)
            {
                int room = MaxCaptured - text.Length;
                text.Append(buffer, 0, Math.Min(read, room));
                if (read > room) truncated = true;
            }
            else
            {
                truncated = true;
            }
        }

        if (truncated) text.Append("\n[output truncated]");
        return new CapturedText(text.ToString(), truncated);
    }

    private sealed record CapturedText(string Text, bool Truncated);
}
