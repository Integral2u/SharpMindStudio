using System.Diagnostics;
using System.Text;
using SharpMind.Core;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Runs a program in a context folder. This is real code with the user's
/// account permissions, so the gate is the approval to run at all, not a
/// sandbox: a started program can do whatever the user can do. No shell is
/// involved, arguments are passed individually, and the tree is killed on
/// timeout.
/// </summary>
public sealed class WorkProcessTools(
    string projectRoot,
    IReadOnlyList<string> allowedRoots,
    Func<WorkToolAccess, Task<bool>>? permission = null,
    ISecretResolver? secrets = null,
    CommandPolicy? policy = null)
{
    private const int MaxOutput = 20_000;

    private readonly string _root = NormalizeRoot(projectRoot);
    private readonly IReadOnlyList<string> _roots =
        [.. allowedRoots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase)];
    private readonly ISecretResolver _secrets = secrets ?? NullSecretResolver.Instance;
    private readonly CommandPolicy? _policy = policy;

    [ToolDesc("Runs a program inside an allowed context folder and returns its output. Use it to build, test or inspect a project. This runs real code with the user's permissions: Studio gates the command, not everything the program touches. No shell is used, so pipes and redirection must be part of the program's own arguments.")]
    public async Task<string> RunProcess(
        [ToolDesc("Executable to run, for example dotnet, git or npm. A bare name is resolved from PATH.")] string fileName,
        [ToolDesc("Arguments as one command-line string. Quotes group a value; no shell expansion happens.")] string arguments = "",
        [ToolDesc("Working directory relative to the project root. Defaults to the project root.")] string workingDirectory = ".",
        [ToolDesc("Seconds before the process tree is killed.")] int timeoutSeconds = 60)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "A program name is required.";

            string cwd = ResolveDirectory(workingDirectory);
            AllowedCommandMatch? match = null;
            if (_policy is { IsEmpty: false })
            {
                match = _policy.Resolve(fileName);
                if (match is null)
                {
                    return $"Command '{fileName}' is not in the allowed list."
                        + (_policy.IsEmpty ? "" : $" Allowed: {_policy.AllowedNames}.");
                }
            }

            string executable = match?.Executable ?? fileName;
            string prefix = match is null
                ? ""
                : string.Join(" ", match.PrefixArguments);
            string commandLine = string.Join(
                " ",
                new[] { fileName, prefix, arguments }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            // The permission prompt shows the reference, never the value.
            IReadOnlyList<string> secretNames = match is null
                ? SecretTemplate.Names(arguments)
                : [.. SecretTemplate.Names(prefix).Concat(SecretTemplate.Names(arguments))];
            string detail = secretNames.Count > 0
                ? $"{commandLine} (uses secret: {string.Join(", ", secretNames)})"
                : commandLine;

            if (!await AllowedAsync(nameof(RunProcess), WorkToolCategory.Execute, cwd, detail))
                return $"Permission denied: RunProcess may not run a program in '{workingDirectory}' with the current project permission.";

            var used = new List<SecretUse>();
            var expanded = new List<string>();
            if (match is not null)
            {
                foreach (string prefixArgument in match.PrefixArguments)
                {
                    SecretExpansion expansion = SecretTemplate.Expand(prefixArgument, _secrets.Resolve);
                    if (expansion.Missing.Count > 0)
                        return new SecretMissingException(expansion.Missing).Message;

                    used.AddRange(expansion.Used);
                    expanded.Add(expansion.Text);
                }
            }

            foreach (string argument in SplitArguments(arguments ?? ""))
            {
                SecretExpansion expansion = SecretTemplate.Expand(argument, _secrets.Resolve);
                if (expansion.Missing.Count > 0)
                    return new SecretMissingException(expansion.Missing).Message;

                used.AddRange(expansion.Used);
                expanded.Add(expansion.Text);
            }

            string[] secretValues = [.. used
                .Select(use => use.Value)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)];

            ProcessStartInfo startInfo = McpCommandResolver.CreateStartInfo(
                executable,
                expanded,
                new Dictionary<string, string>());
            startInfo.WorkingDirectory = cwd;

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    return $"Could not start {executable}.";
            }
            catch (Exception ex)
            {
                return SecretTemplate.Redact(
                    $"Could not start {executable}: {ex.GetBaseException().Message}",
                    secretValues);
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 600);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }

                return $"Timed out after {timeoutSeconds}s; the process tree was killed."
                       + Format(
                           null,
                           SecretTemplate.Redact(await stdout, secretValues),
                           SecretTemplate.Redact(await stderr, secretValues));
            }

            return Format(
                process.ExitCode,
                SecretTemplate.Redact(await stdout, secretValues),
                SecretTemplate.Redact(await stderr, secretValues));
        }
        catch (Exception ex)
        {
            return $"Error running program: {ex.GetBaseException().Message}";
        }
    }

    private static string Format(int? exitCode, string stdout, string stderr)
    {
        var text = new StringBuilder();
        if (exitCode is not null) text.Append($"Exit code {exitCode}.");
        Append(text, "stdout", stdout);
        Append(text, "stderr", stderr);
        return text.Length == 0 ? "No output." : text.ToString();
    }

    private static void Append(StringBuilder text, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (value.Length > MaxOutput)
            value = value[..MaxOutput] + "\n[output truncated]";
        text.Append('\n').Append(label).Append(":\n").Append(value.TrimEnd());
    }

    /// <summary>
    /// Splits a command line the way a shell would split words, but without any
    /// expansion. The program receives the tokens through ArgumentList, so a
    /// value can never become a second command.
    /// </summary>
    private static IEnumerable<string> SplitArguments(string text)
    {
        var current = new StringBuilder();
        bool quoted = false;

        foreach (char character in text)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length == 0) continue;
                yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0) yield return current.ToString();
    }

    private async Task<bool> AllowedAsync(
        string tool,
        WorkToolCategory category,
        string path,
        string? detail = null)
    {
        if (permission is null) return false;
        return await permission(new WorkToolAccess(tool, category, path) { Detail = detail });
    }

    private string ResolveDirectory(string relativePath)
    {
        string candidate = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath.Trim();
        string fullPath = Path.GetFullPath(Path.Combine(_root, candidate));

        bool inside = _roots.Any(root => IsUnderRoot(fullPath, root));
        if (!inside)
            throw new UnauthorizedAccessException(
                $"'{relativePath}' is outside the allowed project locations.");
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Working directory not found: {relativePath}");
        return fullPath;
    }

    private static bool IsUnderRoot(string fullPath, string root)
    {
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return true;
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string full = Path.GetFullPath(root);
        string pathRoot = Path.GetPathRoot(full) ?? "";
        return string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
