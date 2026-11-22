using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class CommandPolicyTests
{
    [Fact]
    public void Resolve_MatchesAnAliasAndKeepsItsFixedPrefix()
    {
        using var temp = new TempDirectory();
        var policy = new CommandPolicy(
            [
                new AllowedCommand
                {
                    Name = "helper",
                    Executable = "dotnet",
                    ArgumentsPrefix = ["tool.dll"],
                },
            ],
            temp.Path);

        AllowedCommandMatch? match = policy.Resolve("helper");

        Assert.NotNull(match);
        Assert.Equal("helper", match.Command.Name);
        Assert.Equal(
            "dotnet",
            Path.GetFileNameWithoutExtension(match.Executable),
            ignoreCase: true);
        Assert.Equal(["tool.dll"], match.PrefixArguments);
    }

    [Fact]
    public void Resolve_RefusesAnUnlistedCommand()
    {
        using var temp = new TempDirectory();
        var policy = new CommandPolicy(
            [new AllowedCommand { Name = "git", Executable = "git" }],
            temp.Path);

        Assert.Null(policy.Resolve("notallowed"));
        Assert.False(policy.Contains("notallowed"));
    }

    [Fact]
    public void Resolve_RefusesABatchOrPowerShellShim()
    {
        using var temp = new TempDirectory();
        string batch = temp.Combine("tool.cmd");
        File.WriteAllText(batch, "@echo off\r\n");
        string script = temp.Combine("tool.ps1");
        File.WriteAllText(script, "Write-Output 'hi'\r\n");
        var policy = new CommandPolicy(
            [
                new AllowedCommand { Name = "batch", Executable = batch },
                new AllowedCommand { Name = "script", Executable = script },
            ],
            temp.Path);

        Assert.Null(policy.Resolve("batch"));
        Assert.Null(policy.Resolve(batch));
        Assert.Null(policy.Resolve("script"));
    }

    [Fact]
    public void Resolve_NeverAllowsAShellEvenWhenListed()
    {
        using var temp = new TempDirectory();
        string shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var policy = new CommandPolicy(
            [new AllowedCommand { Name = "shell", Executable = shell }],
            temp.Path);

        Assert.Null(policy.Resolve("shell"));
        Assert.Null(policy.Resolve(shell));
    }
}
