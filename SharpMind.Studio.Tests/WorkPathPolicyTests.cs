using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class WorkPathPolicyTests
{
    [Fact]
    public void ResolveFile_AllowsTheRootAndItsChildren()
    {
        using var temp = new TempDirectory();
        string child = temp.Combine("sub");
        Directory.CreateDirectory(child);
        var policy = new WorkPathPolicy(temp.Path, [temp.Path]);

        Assert.Equal(
            Path.GetFullPath(child),
            policy.ResolveDirectory("sub"));
        Assert.Equal(
            Path.GetFullPath(temp.Combine("out.txt")),
            policy.ResolveFile("out.txt"));
    }

    [Fact]
    public void ResolveFile_RefusesAParentEscape()
    {
        using var temp = new TempDirectory();
        var policy = new WorkPathPolicy(temp.Path, [temp.Path]);

        Assert.Throws<UnauthorizedAccessException>(
            () => policy.ResolveFile("../escape.txt"));
    }

    [Fact]
    public void ResolveDirectory_RefusesASiblingWithTheSamePrefix()
    {
        using var temp = new TempDirectory();
        string project = temp.Combine("project");
        string sibling = temp.Combine("project-other");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(sibling);
        var policy = new WorkPathPolicy(project, [project]);

        Assert.Throws<UnauthorizedAccessException>(
            () => policy.ResolveDirectory(sibling));
    }
}
