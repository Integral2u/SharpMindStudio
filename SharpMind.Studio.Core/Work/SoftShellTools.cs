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
        + "%vault:NAME%. cd persists across RunShell calls in this session. "
        + "No real shell is used: command substitution, backticks, here-docs, "
        + "background jobs, globbing and shells are refused. Only commands "
        + "allowed for this project can run, and the working directory needs Run "
        + "permission. Prefer the CLI wrappers and the file tools when they fit.")]
    public Task<string> RunShell(
        [ToolDesc("Command line, for example: git status && dotnet build")] string command,
        [ToolDesc(
            "One-off starting directory relative to the session directory. "
            + "Defaults to the current session directory; cd is what persists.")]
        string workingDirectory = ".",
        [ToolDesc("Seconds before the whole command is stopped.")]
        int timeoutSeconds = 60) =>
        runner.RunAsync(command, workingDirectory, timeoutSeconds);
}
