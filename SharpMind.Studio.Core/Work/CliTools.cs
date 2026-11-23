using SharpMind.Core;

namespace SharpMind.Studio.Core.Work;

/// <summary>Read-only repository helpers. They use argv, not the shell parser.</summary>
public sealed class GitCliTools(CliToolRunner runner)
{
    [ToolDesc(
        "Shows the working tree status, branch and short file list. "
        + "Requires git in the project's command line allowlist and Run permission "
        + "for the working directory.")]
    public Task<string> git_status(
        [ToolDesc("Working directory relative to the current session directory.")]
        string workingDirectory = ".") =>
        runner.RunAsync(
            "git_status",
            "git",
            ["status", "--short", "--branch"],
            workingDirectory);

    [ToolDesc(
        "Shows a unified diff. Use staged=true for the index. "
        + "Requires git in the project's command line allowlist and Run permission "
        + "for the working directory.")]
    public Task<string> git_diff(
        [ToolDesc("Path to diff, relative to the working directory. Defaults to the whole tree.")]
        string path = ".",
        [ToolDesc("Diff the staged index instead of the working tree.")]
        bool staged = false,
        [ToolDesc("Working directory relative to the current session directory.")]
        string workingDirectory = ".") =>
        runner.RunAsync(
            "git_diff",
            "git",
            staged ? ["diff", "--cached", "--", path] : ["diff", "--", path],
            workingDirectory);

    [ToolDesc(
        "Shows recent commits one per line. "
        + "Requires git in the project's command line allowlist and Run permission "
        + "for the working directory.")]
    public Task<string> git_log(
        [ToolDesc("Maximum number of commits to show.")]
        int maxCount = 10,
        [ToolDesc("Optional path to limit the history to.")]
        string? path = null,
        [ToolDesc("Working directory relative to the current session directory.")]
        string workingDirectory = ".")
    {
        var arguments = new List<string>
        {
            "log",
            "--oneline",
            $"-n{Math.Clamp(maxCount, 1, 100)}",
        };
        if (!string.IsNullOrWhiteSpace(path))
            arguments.AddRange(["--", path]);

        return runner.RunAsync("git_log", "git", arguments, workingDirectory);
    }
}

/// <summary>Build and test helpers that keep the command line out of the model's hands.</summary>
public sealed class DotnetCliTools(CliToolRunner runner)
{
    [ToolDesc(
        "Builds a .NET project or solution. "
        + "Requires dotnet in the project's command line allowlist and Run "
        + "permission for the working directory.")]
    public Task<string> dotnet_build(
        [ToolDesc("Project or solution path relative to the working directory. Defaults to '.'.")]
        string project = ".",
        [ToolDesc("Build configuration.")]
        string configuration = "Debug",
        [ToolDesc("Skip the implicit restore.")]
        bool noRestore = false,
        [ToolDesc("Working directory relative to the current session directory.")]
        string workingDirectory = ".")
    {
        var arguments = new List<string>
        {
            "build",
            project,
            "--nologo",
            "-v",
            "minimal",
            "-c",
            configuration,
        };
        if (noRestore) arguments.Add("--no-restore");

        return runner.RunAsync("dotnet_build", "dotnet", arguments, workingDirectory, 300);
    }

    [ToolDesc(
        "Runs .NET tests. "
        + "Requires dotnet in the project's command line allowlist and Run "
        + "permission for the working directory.")]
    public Task<string> dotnet_test(
        [ToolDesc("Project or solution path relative to the working directory. Defaults to '.'.")]
        string project = ".",
        [ToolDesc("Build configuration.")]
        string configuration = "Debug",
        [ToolDesc("Working directory relative to the current session directory.")]
        string workingDirectory = ".") =>
        runner.RunAsync(
            "dotnet_test",
            "dotnet",
            ["test", project, "--nologo", "-c", configuration],
            workingDirectory,
            300);
}

/// <summary>Ripgrep search without a shell or glob expansion.</summary>
public sealed class RipgrepCliTools(CliToolRunner runner)
{
    [ToolDesc(
        "Searches files with ripgrep and returns matching lines. "
        + "Requires rg in the project's command line allowlist and Run permission "
        + "for the working directory.")]
    public Task<string> rg_search(
        [ToolDesc("Regular expression to search for.")]
        string pattern,
        [ToolDesc("File or directory to search, relative to the working directory.")]
        string path = ".",
        [ToolDesc("Maximum matches per file.")]
        int maxResults = 50,
        [ToolDesc("Working directory relative to the current session directory.")]
        string workingDirectory = ".") =>
        runner.RunAsync(
            "rg_search",
            "rg",
            [
                "--line-number",
                "--no-heading",
                "--color",
                "never",
                "--max-count",
                Math.Clamp(maxResults, 1, 500).ToString(),
                "--",
                pattern,
                path,
            ],
            workingDirectory);
}
