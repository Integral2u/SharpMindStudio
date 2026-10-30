using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Tests;

public sealed class LegacyStudioCleanupTests
{
    [Fact]
    public void RemoveEmptyProjectFolders_DeletesOnlyEmptyLegacyFolders()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("settings.json");
        File.WriteAllText(settings, "{}");
        Directory.CreateDirectory(temp.Combine("runs", "nested"));
        Directory.CreateDirectory(temp.Combine("chats"));
        Directory.CreateDirectory(temp.Combine("data", "keep"));
        File.WriteAllText(temp.Combine("data", "keep", "note.txt"), "keep me");
        Directory.CreateDirectory(temp.Combine("not-legacy"));

        IReadOnlyList<string> removed = LegacyStudioCleanup.RemoveEmptyProjectFolders(temp.Path);

        Assert.Contains(temp.Combine("runs"), removed);
        Assert.Contains(temp.Combine("chats"), removed);
        Assert.False(Directory.Exists(temp.Combine("runs")));
        Assert.False(Directory.Exists(temp.Combine("chats")));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(temp.Combine("data", "keep", "note.txt")));
        Assert.True(Directory.Exists(temp.Combine("not-legacy")));
    }

    [Fact]
    public void RemoveEmptyProjectFolders_DoesNothingWhenTheRootIsMissing()
    {
        using var temp = new TempDirectory();

        IReadOnlyList<string> removed =
            LegacyStudioCleanup.RemoveEmptyProjectFolders(temp.Combine("missing"));

        Assert.Empty(removed);
    }
}
