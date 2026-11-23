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

    private static WorkCommandRunner Runner(
        TempDirectory temp,
        CommandPolicy? policy,
        Func<string>? currentDirectory = null) =>
        new(
            policy,
            new WorkPathPolicy(temp.Path, [temp.Path]),
            _ => Task.FromResult(true),
            currentDirectory: currentDirectory);
}
