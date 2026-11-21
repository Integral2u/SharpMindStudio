using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Vault;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SecretTemplateTests
{
    [Fact]
    public void Expand_ReplacesAKnownReference()
    {
        SecretExpansion expansion = SecretTemplate.Expand(
            "Authorization: Bearer %vault:GITHUB_TOKEN%",
            Resolver(("GITHUB_TOKEN", "ghp_secret")));

        Assert.Equal("Authorization: Bearer ghp_secret", expansion.Text);
        Assert.True(expansion.IsComplete);
        SecretUse use = Assert.Single(expansion.Used);
        Assert.Equal("GITHUB_TOKEN", use.Name);
        Assert.Equal("ghp_secret", use.Value);
    }

    [Fact]
    public void Expand_KeepsAMissingReferenceAndReportsIt()
    {
        SecretExpansion expansion = SecretTemplate.Expand(
            "curl -H 'Authorization: Bearer %vault:TYPO%'",
            Resolver());

        Assert.Equal("curl -H 'Authorization: Bearer %vault:TYPO%'", expansion.Text);
        Assert.False(expansion.IsComplete);
        Assert.Empty(expansion.Used);
        Assert.Equal(["TYPO"], expansion.Missing);
    }

    [Fact]
    public void Expand_IsCaseInsensitive()
    {
        SecretExpansion expansion = SecretTemplate.Expand(
            "%VAULT:github_token%",
            Resolver(("GITHUB_TOKEN", "ghp_secret")));

        Assert.Equal("ghp_secret", expansion.Text);
        Assert.Single(expansion.Used);
    }

    [Fact]
    public void Expand_DoesNotResolveInsideASecretValue()
    {
        SecretExpansion expansion = SecretTemplate.Expand(
            "%vault:TOKEN%",
            Resolver(("TOKEN", "%vault:OTHER%"), ("OTHER", "must-not-appear")));

        Assert.Equal("%vault:OTHER%", expansion.Text);
        SecretUse use = Assert.Single(expansion.Used);
        Assert.Equal("TOKEN", use.Name);
    }

    [Fact]
    public void Expand_TreatsDoublePercentAsALiteral()
    {
        SecretExpansion expansion = SecretTemplate.Expand(
            "echo %%vault:TOKEN%%",
            Resolver(("TOKEN", "ghp_secret")));

        Assert.Equal("echo %vault:TOKEN%", expansion.Text);
        Assert.Empty(expansion.Used);
        Assert.Empty(expansion.Missing);
    }

    [Fact]
    public void Expand_DeduplicatesRepeatedUses()
    {
        SecretExpansion expansion = SecretTemplate.Expand(
            "%vault:TOKEN% and %vault:TOKEN%",
            Resolver(("TOKEN", "ghp_secret")));

        Assert.Equal("ghp_secret and ghp_secret", expansion.Text);
        Assert.Single(expansion.Used);
    }

    [Fact]
    public void Expand_LeavesOtherPercentSyntaxAlone()
    {
        const string text = "cmd /c echo %PATH% %1 100% done";
        SecretExpansion expansion = SecretTemplate.Expand(text, Resolver());

        Assert.Equal(text, expansion.Text);
        Assert.Empty(expansion.Missing);
        Assert.Empty(SecretTemplate.Names(text));
    }

    [Fact]
    public void Names_IgnoresEscapedReferences()
    {
        IReadOnlyList<string> names = SecretTemplate.Names(
            "%%vault:ESCAPED%% %vault:FIRST% %vault:SECOND% %vault:FIRST%");

        Assert.Equal(["FIRST", "SECOND"], names);
    }

    [Fact]
    public void Redact_ReplacesTheLongestValueFirst()
    {
        string redacted = SecretTemplate.Redact(
            "abcdef and abc",
            ["abc", "abcdef"]);

        Assert.Equal("*** and ***", redacted);
    }

    [Fact]
    public void Redact_IgnoresEmptyValues()
    {
        Assert.Equal("nothing here", SecretTemplate.Redact("nothing here", ["", null!]));
    }

    [Fact]
    public void ExpandJson_WalksObjectsAndArraysWithoutMutatingTheInput()
    {
        JsonNode input = JsonNode.Parse("""
            {
              "headers": { "Authorization": "Bearer %vault:TOKEN%" },
              "items": ["%vault:TOKEN%", 42, true, null],
              "keep": "plain"
            }
            """)!;

        JsonNode? output = SecretTemplate.ExpandJson(
            input,
            Resolver(("TOKEN", "s3cret")),
            out IReadOnlyList<SecretUse> used,
            out IReadOnlyList<string> missing);

        Assert.Empty(missing);
        Assert.Single(used);
        Assert.Equal("Bearer s3cret", output!["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("s3cret", output["items"]![0]!.GetValue<string>());
        Assert.Equal(42, output["items"]![1]!.GetValue<int>());
        Assert.True(output["items"]![2]!.GetValue<bool>());
        Assert.Null(output["items"]![3]);
        Assert.Equal("plain", output["keep"]!.GetValue<string>());

        Assert.Equal(
            "Bearer %vault:TOKEN%",
            input["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Fact]
    public void ExpandJson_ReportsMissingReferencesAcrossTheTree()
    {
        JsonNode input = JsonNode.Parse("""
            { "a": "%vault:ONE%", "b": ["%vault:TWO%", "%vault:ONE%"] }
            """)!;

        JsonNode? output = SecretTemplate.ExpandJson(
            input,
            Resolver(),
            out IReadOnlyList<SecretUse> used,
            out IReadOnlyList<string> missing);

        Assert.Empty(used);
        Assert.Equal(["ONE", "TWO"], missing);
        Assert.Equal("%vault:ONE%", output!["a"]!.GetValue<string>());
    }

    [Fact]
    public void Names_FindsReferencesInAJsonTree()
    {
        JsonNode input = JsonNode.Parse("""
            { "a": "%vault:ONE%", "b": ["%vault:TWO%", "%%vault:ESCAPED%%"], "n": 1 }
            """)!;

        Assert.Equal(["ONE", "TWO"], SecretTemplate.Names(input));
    }

    [Fact]
    public void ExpandJson_HandlesNull()
    {
        JsonNode? output = SecretTemplate.ExpandJson(
            null,
            Resolver(),
            out IReadOnlyList<SecretUse> used,
            out IReadOnlyList<string> missing);

        Assert.Null(output);
        Assert.Empty(used);
        Assert.Empty(missing);
    }

    private static Func<string, string?> Resolver(
        params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(
            value => value.Name,
            value => value.Value,
            StringComparer.OrdinalIgnoreCase);

        return name => map.TryGetValue(name, out string? value) ? value : null;
    }
}
