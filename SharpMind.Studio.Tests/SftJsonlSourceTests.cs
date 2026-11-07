using SharpMind.Data.Records;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// SFT reads chat JSONL in the shapes real instruction datasets use. These tests
/// pin the auto-detection, the assistant-turn requirement and the turn roles the
/// assistant mask depends on.
/// </summary>
public sealed class SftJsonlSourceTests
{
    [Fact]
    public async Task Auto_ReadsMessagesConversationsPromptResponseAndInstructionOutput()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("chats.jsonl");
        await File.WriteAllTextAsync(path, string.Join('\n', [
            """{"messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]}""",
            """{"conversations":[{"from":"human","value":"ping"},{"from":"gpt","value":"pong"}]}""",
            """{"prompt":"q1","response":"a1"}""",
            """{"instruction":"add","input":"1+1","output":"2"}""",
        ]));

        var source = new SftJsonlSource(path);
        List<DataRecord> records = await ReadAll(source);

        Assert.Equal(4, records.Count);
        Assert.Equal(["system", "user", "assistant"], records[0].Turns!.Select(turn => turn.Role).ToArray());
        Assert.Equal("hello", records[0].Turns![2].Content);
        Assert.Equal(["user", "assistant"], records[1].Turns!.Select(turn => turn.Role).ToArray());
        Assert.Equal("pong", records[1].Turns![1].Content);
        Assert.Equal(["user", "assistant"], records[2].Turns!.Select(turn => turn.Role).ToArray());
        Assert.Contains("1+1", records[3].Turns![0].Content);
        Assert.Equal("2", records[3].Turns![1].Content);
        Assert.All(records, record => Assert.Equal(path, record.Source));
        Assert.Contains("assistant: hello", records[0].Text);
    }

    [Fact]
    public async Task Auto_SkipsMalformedAndAssistantLessLines()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("chats.jsonl");
        await File.WriteAllTextAsync(path, string.Join('\n', [
            "not json",
            """{"prompt":"unanswered"}""",
            """{"messages":[{"role":"user","content":"hi"}]}""",
            """{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"there"}]}""",
        ]));

        var source = new SftJsonlSource(path);
        List<DataRecord> records = await ReadAll(source);

        Assert.Single(records);
        Assert.Equal(3, source.SkippedRecords);
        Assert.Equal(1, source.YieldedRecords);
    }

    [Fact]
    public async Task Messages_ReadsContentPartsAndNormalisesShareGptRoles()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("chats.jsonl");
        await File.WriteAllTextAsync(path, string.Join('\n', [
            """{"messages":[{"role":"user","content":[{"type":"text","text":"part one"},{"type":"text","text":"part two"}]},{"role":"model","content":"answer"}]}""",
        ]));

        List<DataRecord> records = await ReadAll(new SftJsonlSource(path));

        DataRecord record = Assert.Single(records);
        Assert.Contains("part one", record.Turns![0].Content);
        Assert.Contains("part two", record.Turns[0].Content);
        Assert.Equal("assistant", record.Turns[1].Role);
    }

    [Fact]
    public async Task ExplicitFormat_DoesNotFallBackToAnotherShape()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("chats.jsonl");
        await File.WriteAllTextAsync(path,
            """{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"there"}]}""");

        var source = new SftJsonlSource(path, "conversations");
        List<DataRecord> records = await ReadAll(source);

        Assert.Empty(records);
        Assert.Equal(1, source.SkippedRecords);
    }

    [Fact]
    public async Task Glob_ReadsEveryMatchedFile()
    {
        using var temp = new TempDirectory();
        string folder = temp.Combine("chats");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(temp.Combine("chats", "a.jsonl"),
            """{"prompt":"a","response":"1"}""");
        await File.WriteAllTextAsync(temp.Combine("chats", "b.jsonl"),
            """{"prompt":"b","response":"2"}""");

        List<DataRecord> records = await ReadAll(new SftJsonlSource(temp.Combine("chats", "*.jsonl")));

        Assert.Equal(2, records.Count);
        Assert.Equal(["1", "2"], records.Select(record => record.Turns![1].Content).OrderBy(value => value).ToArray());
    }

    [Fact]
    public async Task ProvenanceSourceAndId_BecomeTheSplitGroup()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("factory.jsonl");
        await File.WriteAllTextAsync(path, string.Join('\n', [
            """{"messages":[{"role":"user","content":"one"},{"role":"assistant","content":"a"}],"source":"C:\\docs\\one.txt","id":3}""",
            """{"messages":[{"role":"user","content":"two"},{"role":"assistant","content":"b"}],"source":"C:\\docs\\one.txt","id":3}""",
            """{"messages":[{"role":"user","content":"three"},{"role":"assistant","content":"c"}],"source":"C:\\docs\\two.txt","id":0}""",
            """{"prompt":"q","response":"a"}""",
        ]));

        List<DataRecord> records = await ReadAll(new SftJsonlSource(path));

        Assert.Equal(@"C:\docs\one.txt#3", records[0].GroupId);
        Assert.Equal(records[0].GroupId, records[1].GroupId);
        Assert.Equal(@"C:\docs\two.txt#0", records[2].GroupId);
        Assert.Null(records[3].GroupId);
        Assert.All(records, record => Assert.Equal(path, record.Source));
    }

    private static async Task<List<DataRecord>> ReadAll(SftJsonlSource source)
    {
        var records = new List<DataRecord>();
        await foreach (DataRecord record in source.ReadRecordsAsync())
            records.Add(record);
        return records;
    }
}
