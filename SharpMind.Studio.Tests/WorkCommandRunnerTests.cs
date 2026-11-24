using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class WorkCommandRunnerTests
{
    [Fact]
    public async Task Unrestricted_RunsAnUnlistedProgram()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, policy: null);

        WorkCommandResult result = await runner.RunAsync(
            "test", "cmd", ["/c", "echo", "hello"], ".");

        Assert.Null(result.Error);
        Assert.Contains("hello", result.Stdout);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RestrictedEmptyPolicy_RefusesBeforeStarting()
    {
        using var temp = new TempDirectory();
        var policy = new CommandPolicy([], temp.Path);
        var runner = Runner(temp, policy);

        WorkCommandResult result = await runner.RunAsync(
            "test", "cmd", ["/c", "echo", "hello"], ".");

        Assert.NotNull(result.Error);
        Assert.Contains("No commands are allowed", result.Error);
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public async Task WorkingDirectory_DefaultsToTheSessionDirectory()
    {
        using var temp = new TempDirectory();
        string sub = temp.Combine("sub");
        Directory.CreateDirectory(sub);
        var runner = Runner(temp, policy: null, currentDirectory: () => sub);

        WorkCommandResult result = await runner.RunAsync(
            "test", "cmd", ["/c", "cd"], ".");

        Assert.Contains(sub, result.Stdout);
    }

    [Fact]
    public async Task AllowAlways_SkipsThePermissionGate()
    {
        using var temp = new TempDirectory();
        var policy = new CommandPolicy(
            [
                new AllowedCommand
                {
                    Name = "dotnet",
                    Executable = "dotnet",
                    Permission = CommandPermission.AllowAlways,
                },
            ],
            temp.Path);
        var runner = Runner(temp, policy, permission: _ => Task.FromResult(false));

        WorkCommandResult result = await runner.RunAsync(
            "test", "dotnet", ["--version"], ".");

        Assert.Null(result.Error);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Ask_StillUsesThePermissionGate()
    {
        using var temp = new TempDirectory();
        var policy = new CommandPolicy(
            [
                new AllowedCommand
                {
                    Name = "dotnet",
                    Executable = "dotnet",
                    Permission = CommandPermission.Ask,
                },
            ],
            temp.Path);
        var runner = Runner(temp, policy, permission: _ => Task.FromResult(false));

        WorkCommandResult result = await runner.RunAsync(
            "test", "dotnet", ["--version"], ".");

        Assert.NotNull(result.Error);
        Assert.Contains("Permission denied", result.Error);
    }

    [Fact]
    public async Task Allow_SkipsTheGateOnlyWhenTheSessionAllowsIt()
    {
        using var temp = new TempDirectory();
        var policy = new CommandPolicy(
            [
                new AllowedCommand
                {
                    Name = "dotnet",
                    Executable = "dotnet",
                    Permission = CommandPermission.Allow,
                },
            ],
            temp.Path);

        WorkCommandResult denied = await Runner(
            temp, policy, permission: _ => Task.FromResult(false),
            sessionAllowed: _ => false)
            .RunAsync("test", "dotnet", ["--version"], ".");
        Assert.Contains("Permission denied", denied.Error);

        WorkCommandResult allowed = await Runner(
            temp, policy, permission: _ => Task.FromResult(false),
            sessionAllowed: name => name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            .RunAsync("test", "dotnet", ["--version"], ".");
        Assert.Null(allowed.Error);
        Assert.Equal(0, allowed.ExitCode);
    }

    private static WorkCommandRunner Runner(
        TempDirectory temp,
        CommandPolicy? policy,
        Func<string>? currentDirectory = null,
        Func<WorkToolAccess, Task<bool>>? permission = null,
        Func<string, bool>? sessionAllowed = null) =>
        new(
            policy,
            new WorkPathPolicy(temp.Path, [temp.Path]),
            permission ?? (_ => Task.FromResult(true)),
            currentDirectory: currentDirectory,
            sessionAllowed: sessionAllowed);
}
