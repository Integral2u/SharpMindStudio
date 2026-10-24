using SharpMind.Studio.Core.Recipes;

namespace SharpMind.Studio.Tests;

public sealed class RecipeTests
{
    private const string ValidRecipe = """
        {
          "schema": "smmtx/1",
          "name": "kiosk-assistant",
          "deviceProfile": "kiosk-4gb",
          "steps": [
            { "id": "data", "type": "ingest", "params": { "sources": ["client-docs/"] } },
            { "id": "clean", "type": "triage", "needs": ["data"], "params": { "dedupe": "near" } },
            { "id": "tune", "type": "lora", "needs": ["clean"], "base": "models/v2#master", "params": { "rank": 16 } }
          ]
        }
        """;

    [Fact]
    public void Validator_AcceptsAConnectedGraph()
    {
        var result = RecipeValidator.Validate(RecipeStore.Parse(ValidRecipe));
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void Validator_RejectsMissingDependenciesDuplicateIdsAndCycles()
    {
        var missing = RecipeStore.Parse("""
            { "schema": "smmtx/1", "name": "x", "steps": [
              { "id": "a", "type": "ingest", "needs": ["missing"] } ] }
            """);
        Assert.Contains(RecipeValidator.Validate(missing).Issues, i => i.Code == "step.missing");

        var duplicate = RecipeStore.Parse("""
            { "schema": "smmtx/1", "name": "x", "steps": [
              { "id": "a", "type": "ingest" }, { "id": "a", "type": "ingest" } ] }
            """);
        Assert.Contains(RecipeValidator.Validate(duplicate).Issues, i => i.Code == "step.duplicate");

        var cycle = RecipeStore.Parse("""
            { "schema": "smmtx/1", "name": "x", "steps": [
              { "id": "a", "type": "ingest", "needs": ["b"] },
              { "id": "b", "type": "triage", "needs": ["a"] } ] }
            """);
        Assert.Contains(RecipeValidator.Validate(cycle).Issues, i => i.Code == "step.cycle");
    }

    [Fact]
    public void Validator_RejectsBadIdsAndMissingSchema()
    {
        var recipe = RecipeStore.Parse("""
            { "schema": "smmtx/1", "name": "x", "steps": [
              { "id": "Bad Id", "type": "ingest" } ] }
            """);
        var result = RecipeValidator.Validate(recipe);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "step.id");

        var old = RecipeStore.Parse("""
            { "schema": "smmtx/0", "name": "x", "steps": [ { "id": "a", "type": "ingest" } ] }
            """);
        Assert.Contains(RecipeValidator.Validate(old).Issues, i => i.Code == "schema");
    }

    [Fact]
    public void Hash_IsStableAcrossKeyOrderAndWhitespace()
    {
        var a = RecipeStore.Parse(ValidRecipe);
        var b = RecipeStore.Parse("""
            { "name": "kiosk-assistant", "steps": [
                { "type": "lora", "params": { "rank": 16 }, "base": "models/v2#master", "needs": ["clean"], "id": "tune" },
                { "id": "data", "type": "ingest", "params": { "sources": ["client-docs/"] } },
                { "id": "clean", "type": "triage", "needs": ["data"], "params": { "dedupe": "near" } }
              ], "deviceProfile": "kiosk-4gb", "schema": "smmtx/1" }
            """);

        Assert.Equal(RecipeHasher.Hash(a), RecipeHasher.Hash(b));
    }

    [Fact]
    public void Hash_ChangesWhenAParameterChanges()
    {
        var a = RecipeStore.Parse(ValidRecipe);
        var b = RecipeStore.Parse(ValidRecipe.Replace("\"rank\": 16", "\"rank\": 32", StringComparison.Ordinal));
        Assert.NotEqual(RecipeHasher.Hash(a), RecipeHasher.Hash(b));
    }

    [Fact]
    public void Store_RoundTripsThroughDisk()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("demo.smmtx");
        var recipe = RecipeStore.Parse(ValidRecipe);

        RecipeStore.Save(path, recipe);
        var loaded = RecipeStore.Load(path);

        Assert.Equal(recipe.Name, loaded.Name);
        Assert.Equal(recipe.Steps.Count, loaded.Steps.Count);
        Assert.Equal(RecipeHasher.Hash(recipe), RecipeHasher.Hash(loaded));
    }
}
