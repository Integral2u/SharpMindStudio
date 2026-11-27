using SharpMind.Studio.Core.HuggingFace;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ModelPublishHistoryTests
{
    [Fact]
    public void Store_PersistsNewestFirstAndSurvivesRestart()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("publish-history.json");
        var store = new ModelPublishHistoryStore(path);
        var first = new ModelPublishRecord
        {
            RepoId = "integral2u/one",
            RepoUrl = "https://huggingface.co/integral2u/one",
            CommitUrl = "https://huggingface.co/integral2u/one/commit/a",
            ArtifactFileName = "one_Q8_0.gguf",
            PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var second = first with
        {
            RepoId = "integral2u/two",
            RepoUrl = "https://huggingface.co/integral2u/two",
            CommitUrl = "https://huggingface.co/integral2u/two/commit/b",
            ArtifactFileName = "two_Q4_K.gguf",
            PublishedUtc = DateTimeOffset.UtcNow,
        };

        store.Add(first);
        store.Add(second);

        IReadOnlyList<ModelPublishRecord> loaded =
            new ModelPublishHistoryStore(path).Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("integral2u/two", loaded[0].RepoId);
        Assert.Equal("integral2u/one", loaded[1].RepoId);

        store.Clear();
        Assert.Empty(new ModelPublishHistoryStore(path).Load());
    }
}
