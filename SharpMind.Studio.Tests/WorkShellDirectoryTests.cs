using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class WorkShellDirectoryTests
{
    [Fact]
    public void Current_IsTheRootUntilASessionChangesIt()
    {
        using var temp = new TempDirectory();
        var store = new WorkShellDirectoryStore();
        var paths = new WorkPathPolicy(temp.Path, [temp.Path]);
        var shell = new WorkShellDirectory(store, "session", paths);

        Assert.Equal(paths.Root, shell.Current);

        string sub = temp.Combine("sub");
        Directory.CreateDirectory(sub);
        shell.Set(sub);

        Assert.Equal(sub, shell.Current);
    }

    [Fact]
    public void Sessions_AreIsolatedAndResetIndependently()
    {
        using var temp = new TempDirectory();
        string sub = temp.Combine("sub");
        Directory.CreateDirectory(sub);
        var store = new WorkShellDirectoryStore();
        var paths = new WorkPathPolicy(temp.Path, [temp.Path]);
        var first = new WorkShellDirectory(store, "first", paths);
        var second = new WorkShellDirectory(store, "second", paths);

        first.Set(sub);

        Assert.Equal(sub, first.Current);
        Assert.Equal(paths.Root, second.Current);

        first.Reset();
        Assert.Equal(paths.Root, first.Current);
    }

    [Fact]
    public void Current_ResetsWhenTheDirectoryDisappears()
    {
        using var temp = new TempDirectory();
        string sub = temp.Combine("sub");
        Directory.CreateDirectory(sub);
        var store = new WorkShellDirectoryStore();
        var paths = new WorkPathPolicy(temp.Path, [temp.Path]);
        var shell = new WorkShellDirectory(store, "session", paths);

        shell.Set(sub);
        Directory.Delete(sub);

        Assert.Equal(paths.Root, shell.Current);
        Assert.Null(store.Get("session"));
    }

    [Fact]
    public void Clear_ForgetsEverySession()
    {
        using var temp = new TempDirectory();
        string sub = temp.Combine("sub");
        Directory.CreateDirectory(sub);
        var store = new WorkShellDirectoryStore();
        store.Set("first", sub);
        store.Set("second", sub);

        store.Clear();

        Assert.Null(store.Get("first"));
        Assert.Null(store.Get("second"));
    }
}
