using SharpMind.Core;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// The shell-shaped tool: familiar pipes and redirection, parsed and proxied by
/// Studio. It never hands the string to a real shell.
/// </summary>
public sealed class SoftShellTools(SoftShellRunner runner)
{
    [ToolDesc(
        "Runs a POSIX-style command line inside the project. Supports pipes, "
        + "> >> < 2> 2>&1, && || ;, cd, pwd, echo, inline NAME=value and "
        + "%vault:NAME%. No real shell is used: command substitution, backticks, "
        + "here-docs, background jobs, globbing and shells are refused. Only "
        + "commands allowed for this project can run. Prefer the CLI wrappers "
        + "and the file tools when they fit.")]
    public Task<string> RunShell(
        [ToolDesc("Command line, for example: git status && dotnet build")] string command,
        [ToolDesc("Working directory relative to the project root. Defaults to the root.")]
        string workingDirectory = ".",
        [ToolDesc("Seconds before the whole command is stopped.")]
        int timeoutSeconds = 60) =>
        runner.RunAsync(command, workingDirectory, timeoutSeconds);
}
