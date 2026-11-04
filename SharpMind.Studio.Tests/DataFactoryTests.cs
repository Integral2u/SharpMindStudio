using System.Text.Json;
using SharpMind.Studio.Core.DataFactory;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class DataFactoryTests
{
    [Fact]
    public void RecipeStore_RoundTrips()
    {
        using var temp = new TempDirectory();
        var store = new DataFactoryRecipeStore(temp.Path);
        var recipe = new DataFactoryRecipe
        {
            Name = "wiki-qa",
            SourcePath = @"C:\corpus",
            Provider = DataFactoryProviderKind.OpenRouter,
            RemoteModel = "openai/gpt-4o-mini",
            OutputFormat = DataFactoryOutputFormat.ChatJsonl,
            MaxRecords = 12,
        };

        string path = store.Save(recipe);
        Assert.True(File.Exists(path));
        Assert.Single(store.List());

        DataFactoryRecipe loaded = store.Load(path);
        Assert.Equal("wiki-qa", loaded.Name);
        Assert.Equal(DataFactoryProviderKind.OpenRouter, loaded.Provider);
        Assert.Equal("openai/gpt-4o-mini", loaded.RemoteModel);
        Assert.Equal(12, loaded.MaxRecords);

        store.Delete(path);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Input_CleansFiltersAndDeduplicates()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(temp.Combine("a.txt"),
            "This is a long enough document about cats and dogs and the park. It repeats later.");
        await File.WriteAllTextAsync(temp.Combine("b.txt"),
            "This is a long enough document about cats and dogs and the park. It repeats later.");
        await File.WriteAllTextAsync(temp.Combine("short.txt"), "too short");
        await File.WriteAllTextAsync(temp.Combine("drop.txt"),
            "DROP this long enough document about cats and dogs and the park.");

        var recipe = new DataFactoryRecipe
        {
            SourcePath = temp.Path,
            SourceFormat = DataFactorySourceFormat.Text,
            MinChars = 30,
            DropPattern = "DROP",
            Deduplicate = true,
            NormalizeText = true,
        };

        var documents = new List<DataFactoryDocument>();
        await foreach (DataFactoryDocument document in DataFactoryInput.ReadAsync(recipe))
            documents.Add(document);

        DataFactoryDocument only = Assert.Single(documents);
        Assert.Contains("cats and dogs", only.Text);
        Assert.EndsWith("a.txt", only.Source);
    }

    [Fact]
    public async Task Input_JsonlReadsConfiguredTextField()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("corpus.jsonl");
        await File.WriteAllLinesAsync(path,
        [
            """{"text":"A sufficiently long JSONL document about local models and training."}""",
            """{"body":"not this field"}""",
            "not json",
        ]);

        var recipe = new DataFactoryRecipe
        {
            SourcePath = path,
            SourceFormat = DataFactorySourceFormat.Jsonl,
            TextField = "text",
            MinChars = 20,
        };

        var documents = new List<DataFactoryDocument>();
        await foreach (DataFactoryDocument document in DataFactoryInput.ReadAsync(recipe))
            documents.Add(document);

        Assert.Single(documents);
        Assert.Contains("local models", documents[0].Text);
    }

    [Fact]
    public void Prompt_RendersPlaceholdersAndAppendsMissingText()
    {
        var document = new DataFactoryDocument(7, "Body text", @"C:\corpus\a.txt");

        string rendered = DataFactoryPrompt.Render(
            "Source {name} id {id}: {text}", document);

        Assert.Contains("Source a.txt id 7: Body text", rendered);

        string appended = DataFactoryPrompt.Render("Summarise this.", document);
        Assert.Contains("Summarise this.", appended);
        Assert.Contains("Body text", appended);
    }

    [Fact]
    public void Output_ParsesJsonFencesAndQaPairs()
    {
        Assert.True(DataFactoryOutput.TryParseChat(
            """{"instruction":"Q1","answer":"A1"}""", out string instruction, out string answer));
        Assert.Equal("Q1", instruction);
        Assert.Equal("A1", answer);

        Assert.True(DataFactoryOutput.TryParseChat(
            """
            ```json
            {"question":"Q2","response":"A2"}
            ```
            """, out instruction, out answer));
        Assert.Equal("Q2", instruction);
        Assert.Equal("A2", answer);

        Assert.True(DataFactoryOutput.TryParseChat(
            "Q: Q3\nA: A3", out instruction, out answer));
        Assert.Equal("Q3", instruction);
        Assert.Equal("A3", answer);

        Assert.False(DataFactoryOutput.TryParseChat("just prose", out _, out _));
    }

    [Fact]
    public async Task Runner_WritesChatSamples()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("source.txt");
        await File.WriteAllTextAsync(source,
            "A long enough source document about the ocean and its creatures.");
        string output = temp.Combine("out.jsonl");

        var recipe = new DataFactoryRecipe
        {
            Name = "chat",
            SourcePath = source,
            SourceFormat = DataFactorySourceFormat.Text,
            MinChars = 20,
            OutputFormat = DataFactoryOutputFormat.ChatJsonl,
            OutputPath = output,
            ChatSystemPrompt = "Be brief.",
            MaxRecords = 5,
        };

        var generator = new FakeGenerator(_ =>
            """{"instruction":"What is in the ocean?","answer":"Many creatures."}""");

        DataFactoryRunResult result = await DataFactoryRunner.RunAsync(recipe, generator);

        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.ParseFailures);
        string line = Assert.Single(await File.ReadAllLinesAsync(output));
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement messages = document.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Be brief.", messages[0].GetProperty("content").GetString());
        Assert.Equal("What is in the ocean?", messages[1].GetProperty("content").GetString());
        Assert.Equal("Many creatures.", messages[2].GetProperty("content").GetString());
        Assert.Single(result.Samples);
    }

    [Fact]
    public async Task Runner_WritesTextJsonl()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("source.txt");
        await File.WriteAllTextAsync(source,
            "A long enough source document about the mountains and the snow.");
        string output = temp.Combine("out.jsonl");

        var recipe = new DataFactoryRecipe
        {
            Name = "text",
            SourcePath = source,
            SourceFormat = DataFactorySourceFormat.Text,
            MinChars = 20,
            OutputFormat = DataFactoryOutputFormat.TextJsonl,
            OutputPath = output,
            MaxRecords = 5,
        };

        var generator = new FakeGenerator(_ => "A polished training document.");
        DataFactoryRunResult result = await DataFactoryRunner.RunAsync(recipe, generator);

        Assert.Equal(1, result.Written);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(output)));
        Assert.Equal("A polished training document.", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Runner_CountsChatParseFailures()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("source.txt");
        await File.WriteAllTextAsync(source,
            "A long enough source document about the desert and the heat.");
        string output = temp.Combine("out.jsonl");

        var recipe = new DataFactoryRecipe
        {
            SourcePath = source,
            SourceFormat = DataFactorySourceFormat.Text,
            MinChars = 20,
            OutputFormat = DataFactoryOutputFormat.ChatJsonl,
            OutputPath = output,
            MaxRecords = 5,
        };

        DataFactoryRunResult result = await DataFactoryRunner.RunAsync(
            recipe, new FakeGenerator(_ => "not a chat pair"));

        Assert.Equal(0, result.Written);
        Assert.Equal(1, result.ParseFailures);
    }

    private sealed class FakeGenerator(Func<string, string> generate) : IDataFactoryGenerator
    {
        public string Description => "fake";

        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            DataFactoryRecipe recipe,
            CancellationToken cancellationToken = default)
            => Task.FromResult(generate(userPrompt));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
