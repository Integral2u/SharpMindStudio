using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Tests;

public sealed class StudioPathsTests
{
    [Fact]
    public void EnsureCreated_MakesTheSharedSharpMindFolders()
    {
        using var temp = new TempDirectory();
        StudioPaths.UseRoot(temp.Path);

        StudioPaths.EnsureCreated();

        Assert.Equal(temp.Path, StudioPaths.Root);
        Assert.True(Directory.Exists(StudioPaths.Models));
        Assert.True(Directory.Exists(StudioPaths.Corpus));
        Assert.True(Directory.Exists(StudioPaths.Training));
        Assert.True(Directory.Exists(StudioPaths.ChatSessions));
        Assert.True(Directory.Exists(StudioPaths.Projects));
    }
}
