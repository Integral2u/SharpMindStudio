using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Tests;

public sealed class RunEventTests
{
    [Fact]
    public async Task WriterAndReader_RoundTripTypedEvents()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("events.jsonl");

        await using (var writer = new RunEventWriter(path))
        {
            await writer.WriteAsync(new RunStarted
            {
                RunId = "run-1",
                Kind = "model-bench",
                Subject = "tiny.gguf",
                BaseModelHash = "abc",
                Device = "test",
            });
            await writer.WriteAsync(new StepLogged
            {
                Step = 1,
                TokensThisStep = 1,
                TokensSeen = 17,
                TokensPerSecond = 10.5f,
            });
            await writer.WriteAsync(new RunFinished
            {
                Status = "succeeded",
                DurationMs = 123,
                AtUtc = DateTimeOffset.UtcNow,
            });
        }

        var events = new List<RunEvent>();
        await foreach (var runEvent in RunEventReader.ReadAsync(path))
            events.Add(runEvent);

        Assert.Equal(3, events.Count);
        var started = Assert.IsType<RunStarted>(events[0]);
        Assert.Equal("model-bench", started.Kind);
        Assert.Equal("abc", started.BaseModelHash);

        var step = Assert.IsType<StepLogged>(events[1]);
        Assert.Equal(17, step.TokensSeen);
        Assert.Equal(10.5f, step.TokensPerSecond);

        var finished = Assert.IsType<RunFinished>(events[2]);
        Assert.Equal("succeeded", finished.Status);
    }

    [Fact]
    public async Task Catalog_DerivesStatusStepsAndThroughput()
    {
        using var temp = new TempDirectory();
        var store = new RunStore(temp.Combine("runs"));
        var run = store.Create("model-bench", "tiny");
        var start = DateTimeOffset.UtcNow;
        await using (var writer = run.OpenWriter())
        {
            await writer.WriteAsync(new RunStarted
            {
                RunId = run.RunId,
                Kind = "model-bench",
                Subject = "tiny.gguf",
                Device = "test",
                AtUtc = start,
            });
            await writer.WriteAsync(new StepLogged
            {
                Step = 1,
                TokensSeen = 1,
                TokensThisStep = 1,
                TokensPerSecond = 10,
                AtUtc = start.AddMilliseconds(100),
            });
            await writer.WriteAsync(new StepLogged
            {
                Step = 2,
                TokensSeen = 2,
                TokensThisStep = 1,
                TokensPerSecond = 10,
                AtUtc = start.AddMilliseconds(200),
            });
            await writer.WriteAsync(new ProbeAnswered
            {
                Step = 2,
                ProbeId = "greedy-0",
                Text = "ok",
            });
            await writer.WriteAsync(new ToolProbeCompleted
            {
                Status = "Yes",
                Detail = "toolCalled=true",
                ToolRunId = "tool-run",
                AtUtc = start.AddMilliseconds(250),
            });
            await writer.WriteAsync(new RunFinished
            {
                Status = "succeeded",
                DurationMs = 300,
                AtUtc = start.AddMilliseconds(300),
            });
        }

        var summary = await RunCatalog.LoadAsync(run);
        Assert.Equal("succeeded", summary.Status);
        Assert.Equal(2, summary.Steps);
        Assert.Equal("ok", summary.ProbeText);
        Assert.Equal("Yes", summary.ToolStatus);
        Assert.Equal("tool-run", summary.ToolRunId);
        Assert.Equal(10, summary.TokensPerSecond, precision: 3);
    }

    [Fact]
    public void Store_CreatesOpensAndListsRuns()
    {
        using var temp = new TempDirectory();
        var store = new RunStore(temp.Combine("runs"));
        var first = store.Create("model-bench", "Qwen2-0.5B.Q8_0");

        Assert.True(Directory.Exists(first.Directory));
        Assert.EndsWith(".jsonl", first.EventsPath, StringComparison.OrdinalIgnoreCase);

        var opened = store.Open(first.RunId);
        Assert.Equal(first.Directory, opened.Directory);

        var listed = store.List();
        Assert.Contains(listed, handle => handle.RunId == first.RunId);
    }

    [Fact]
    public void Store_DeletesRunsInsideItsRoot()
    {
        using var temp = new TempDirectory();
        var store = new RunStore(temp.Combine("runs"));
        var run = store.Create("model-bench", "delete-me");

        store.Delete(run.RunId);

        Assert.False(Directory.Exists(run.Directory));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Store_RejectsUnknownRuns()
    {
        using var temp = new TempDirectory();
        var store = new RunStore(temp.Combine("runs"));
        Assert.Throws<DirectoryNotFoundException>(() => store.Open("does-not-exist"));
    }
}

