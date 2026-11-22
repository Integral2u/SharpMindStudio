using System.Text;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Work;

/// <summary>Formats and redacts the result of a curated CLI wrapper.</summary>
public sealed class CliToolRunner(WorkCommandRunner runner)
{
    private const int MaxOutput = 20_000;

    public async Task<string> RunAsync(
        string toolName,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutSeconds = 120)
    {
        WorkCommandResult result = await runner.RunAsync(
            toolName,
            executable,
            arguments,
            workingDirectory,
            timeoutSeconds: timeoutSeconds).ConfigureAwait(false);

        if (result.Error is not null) return result.Error;

        var text = new StringBuilder();
        if (result.TimedOut)
            text.Append($"Timed out after {timeoutSeconds}s; the process tree was killed.\n");
        text.Append($"Exit code {result.ExitCode}.\n");
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            text.Append("\nstdout:\n").Append(result.Stdout.TrimEnd()).Append('\n');
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            text.Append("\nstderr:\n").Append(result.Stderr.TrimEnd()).Append('\n');

        string output = text.Length == 0 ? "No output." : text.ToString().TrimEnd();
        if (output.Length > MaxOutput)
            output = output[..MaxOutput] + "\n[output truncated]";

        return SecretTemplate.Redact(output, result.SecretValues);
    }
}
