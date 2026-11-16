using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Inference;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class CompactorResolverTests
{
    [Fact]
    public void Resolve_MapsTheBuiltInNames()
    {
        Assert.Null(CompactorResolver.Resolve("None"));
        Assert.IsType<SummarizingCompactor>(CompactorResolver.Resolve("Summarize"));
        Assert.IsType<TruncatingCompactor>(CompactorResolver.Resolve("Truncate"));
        Assert.Null(CompactorResolver.Resolve("Unknown"));
        Assert.Null(CompactorResolver.Resolve(""));
    }

    [Fact]
    public void Resolve_FindsAPluginByName()
    {
        var plugin = new FakeCompactor("Custom");
        Assert.Same(plugin, CompactorResolver.Resolve("custom", [plugin]));
    }

    private sealed class FakeCompactor(string name) : IContextCompactor
    {
        public string Name { get; } = name;

        public Task<bool> ShouldCompactAsync(CompactionContext context, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<bool> CompactAsync(CompactionContext context, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
