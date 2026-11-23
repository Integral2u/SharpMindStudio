using SharpMind.Studio.Core.Vault;
using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SoftShellRunnerTests
{
    [Fact]
    public async Task Run_EchoesOutput()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("helper echo hello");

        Assert.Contains("hello", result);
        Assert.Contains("Exit code 0", result);
    }

    [Fact]
    public async Task Run_PipesBufferedOutput()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("helper echo hello | helper cat");

        Assert.Contains("hello", result);
        Assert.DoesNotContain("not allowed", result);
    }

    [Fact]
    public async Task Run_RedirectsToAFileAndBack()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string write = await runner.RunAsync("helper echo hello > out.txt");
        Assert.Contains("Exit code 0", write);
        Assert.Equal($"hello{Environment.NewLine}", File.ReadAllText(temp.Combine("out.txt")));

        string read = await runner.RunAsync("helper cat < out.txt");
        Assert.Contains("hello", read);
    }

    [Fact]
    public async Task Run_ShortCircuitsAndOr()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string andResult = await runner.RunAsync("helper exit 1 && helper echo no");
        Assert.DoesNotContain("no", andResult);

        string orResult = await runner.RunAsync("helper exit 1 || helper echo yes");
        Assert.Contains("yes", orResult);
        Assert.Contains("Exit code 0", orResult);
    }

    [Fact]
    public async Task Run_ExposesTheLastExitCodeToTheNextCommand()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("helper exit 3 || echo $?");

        Assert.Contains("\n3", result);
    }

    [Fact]
    public async Task Run_RedactsVaultValuesAndShowsTheReferenceInThePrompt()
    {
        using var temp = new TempDirectory();
        var details = new List<string>();
        var secrets = new TestSecretResolver(("TOKEN", "s3cret-value"));
        SoftShellRunner runner = Runner(
            temp,
            secrets,
            access =>
            {
                details.Add(access.Detail ?? "");
                return Task.FromResult(true);
            });

        string result = await runner.RunAsync("helper echo %vault:TOKEN%");

        Assert.Contains("***", result);
        Assert.DoesNotContain("s3cret-value", result);
        Assert.Contains(details, detail => detail.Contains("%vault:TOKEN%"));
        Assert.DoesNotContain(details, detail => detail.Contains("s3cret-value"));
    }

    [Fact]
    public async Task Run_RedirectionRequiresWritePermission()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(
            temp,
            permission: access => Task.FromResult(
                access.Category == WorkToolCategory.Execute));

        string result = await runner.RunAsync("helper echo hello > out.txt");

        Assert.Contains("Permission denied writing", result);
        Assert.False(File.Exists(temp.Combine("out.txt")));
    }

    [Fact]
    public async Task Run_RefusesRedirectionOutsideTheProject()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("helper echo hello > ../escape.txt");

        Assert.Contains("outside the allowed project locations", result);
        Assert.False(File.Exists(Path.Combine(temp.Path, "..", "escape.txt")));
    }

    [Fact]
    public async Task Run_ChangesDirectoryInsideTheCommand()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("sub"));
        SoftShellRunner runner = Runner(temp);

        string cwd = await runner.RunAsync("cd sub && helper cwd");
        Assert.Contains("sub", cwd);

        string root = await runner.RunAsync("pwd");
        Assert.Contains(temp.Path, root);
    }

    [Fact]
    public async Task Run_RefusesAnUnlistedCommand()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("notallowed echo nope");

        Assert.Contains("not in the allowed list", result);
    }

    [Fact]
    public async Task Run_RefusesARealShell()
    {
        using var temp = new TempDirectory();
        var policy = new CommandPolicy(
            [
                ShellHelper.Command(),
                new AllowedCommand
                {
                    Name = "shell",
                    Executable = "cmd",
                },
            ],
            temp.Path);
        SoftShellRunner runner = new(
            new WorkPathPolicy(temp.Path, [temp.Path]),
            policy,
            _ => Task.FromResult(true));

        string result = await runner.RunAsync("shell /c echo hi");

        Assert.Contains("not in the allowed list", result);
    }

    [Fact]
    public async Task Run_MergesStderrIntoStdout()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("helper leak hello 2>&1");

        Assert.Contains("hello", result);
        Assert.Contains("stderr:hello", result);
    }

    [Fact]
    public async Task Run_PassesInlineEnvironment()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("HELPER_VALUE=hello helper env HELPER_VALUE");

        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task Run_TimesOut()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("helper sleep 3000", timeoutSeconds: 1);

        Assert.Contains("Timed out", result);
    }

    [Fact]
    public async Task Run_MakesRenamesAndRemovesADirectory()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string mkdir = await runner.RunAsync("mkdir sub");
        Assert.Contains("Exit code 0", mkdir);
        Assert.True(Directory.Exists(temp.Combine("sub")));

        string rename = await runner.RunAsync("rename sub renamed");
        Assert.Contains("Exit code 0", rename);
        Assert.True(Directory.Exists(temp.Combine("renamed")));
        Assert.False(Directory.Exists(temp.Combine("sub")));

        string rmdir = await runner.RunAsync("rmdir renamed");
        Assert.Contains("Exit code 0", rmdir);
        Assert.False(Directory.Exists(temp.Combine("renamed")));
    }

    [Fact]
    public async Task Run_MkdirWithParentsCreatesTheTree()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("mkdir -p one/two/three");

        Assert.Contains("Exit code 0", result);
        Assert.True(Directory.Exists(temp.Combine("one", "two", "three")));
    }

    [Fact]
    public async Task Run_MkdirRequiresWritePermission()
    {
        using var temp = new TempDirectory();
        SoftShellRunner runner = Runner(
            temp,
            permission: access => Task.FromResult(access.Category == WorkToolCategory.Execute));

        string result = await runner.RunAsync("mkdir sub");

        Assert.Contains("Permission denied writing", result);
        Assert.False(Directory.Exists(temp.Combine("sub")));
    }

    [Fact]
    public async Task Run_RmdirRefusesANonEmptyDirectory()
    {
        using var temp = new TempDirectory();
        string sub = temp.Combine("sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "file.txt"), "x");
        SoftShellRunner runner = Runner(temp);

        string result = await runner.RunAsync("rmdir sub");

        Assert.Contains("empty directory", result);
        Assert.True(Directory.Exists(sub));
    }

    [Fact]
    public async Task Run_CdPersistsForTheSessionAndStartsANewSessionAtTheRoot()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("sub"));
        var store = new WorkShellDirectoryStore();
        var paths = new WorkPathPolicy(temp.Path, [temp.Path]);
        var session = new WorkShellDirectory(store, "session-a", paths);
        SoftShellRunner runner = Runner(temp, directory: session);

        await runner.RunAsync("cd sub");

        string sameSession = await runner.RunAsync("pwd");
        string subPath = Path.Combine(temp.Path, "sub");
        Assert.Contains(subPath, sameSession);

        var otherSession = new WorkShellDirectory(store, "session-b", paths);
        string freshSession = await Runner(temp, directory: otherSession).RunAsync("pwd");
        Assert.Contains(temp.Path, freshSession);
        Assert.DoesNotContain(subPath, freshSession);
    }

    [Fact]
    public async Task Run_ExplicitWorkingDirectoryIsOneOffWhileCdPersists()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("sub"));
        var paths = new WorkPathPolicy(temp.Path, [temp.Path]);
        var session = new WorkShellDirectory(new WorkShellDirectoryStore(), "session", paths);
        SoftShellRunner runner = Runner(temp, directory: session);

        await runner.RunAsync("cd sub");

        string oneOff = await runner.RunAsync("pwd", workingDirectory: "..");
        string subPath = Path.Combine(temp.Path, "sub");
        Assert.DoesNotContain(subPath, oneOff);

        string persisted = await runner.RunAsync("pwd");
        Assert.Contains(subPath, persisted);
    }

    [Fact]
    public async Task Run_CdPersistsWhenALaterCommandFails()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("sub"));
        var paths = new WorkPathPolicy(temp.Path, [temp.Path]);
        var session = new WorkShellDirectory(new WorkShellDirectoryStore(), "session", paths);
        SoftShellRunner runner = Runner(temp, directory: session);

        await runner.RunAsync("cd sub && helper exit 3");
        string result = await runner.RunAsync("pwd");

        Assert.Contains(Path.Combine(temp.Path, "sub"), result);
    }

    [Fact]
    public async Task Run_DeletedSessionDirectoryFallsBackToTheRoot()
    {
        using var temp = new TempDirectory();
        string sub = temp.Combine("sub");
        Directory.CreateDirectory(sub);
        var store = new WorkShellDirectoryStore();
        var paths = new WorkPathPolicy(temp.Path, [temp.Path]);
        var session = new WorkShellDirectory(store, "session", paths);
        SoftShellRunner runner = Runner(temp, directory: session);

        await runner.RunAsync("cd sub");
        Directory.Delete(sub);

        string result = await runner.RunAsync("pwd");

        Assert.Contains(temp.Path, result);
        Assert.DoesNotContain(sub, result);
        Assert.Null(store.Get("session"));
    }

    private static SoftShellRunner Runner(
        TempDirectory temp,
        ISecretResolver? secrets = null,
        Func<WorkToolAccess, Task<bool>>? permission = null,
        WorkShellDirectory? directory = null)
    {
        var policy = new CommandPolicy([ShellHelper.Command()], temp.Path);
        return new SoftShellRunner(
            new WorkPathPolicy(temp.Path, [temp.Path]),
            policy,
            permission ?? (_ => Task.FromResult(true)),
            secrets,
            directory);
    }
}
