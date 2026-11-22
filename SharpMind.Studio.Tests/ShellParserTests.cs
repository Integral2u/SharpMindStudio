using SharpMind.Studio.Core.Vault;
using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ShellParserTests
{
    [Fact]
    public void Parse_ReadsACommandAndArguments()
    {
        ShellScript script = ShellParser.Parse("helper echo hello");
        ShellCommand command = Assert.Single(script.Lists).Pipeline.Commands[0];

        Assert.Equal("helper", command.Words[0].Raw);
        Assert.Equal(["echo", "hello"], command.Words.Skip(1).Select(word => word.Raw));
    }

    [Fact]
    public void Parse_ReadsQuotedWordsAndPreservesWindowsPaths()
    {
        ShellScript script = ShellParser.Parse(
            """helper echo "hello world" C:\work\file.txt '$HOME'""");
        ShellCommand command = script.Lists[0].Pipeline.Commands[0];

        Assert.Equal("hello world", command.Words[2].Raw);
        Assert.Equal(@"C:\work\file.txt", command.Words[3].Raw);
        Assert.Equal("$HOME", command.Words[4].Raw);
        Assert.DoesNotContain(
            command.Words[4].Parts,
            part => part is ShellVariable);
    }

    [Fact]
    public void Parse_ReadsVariablesInsideDoubleQuotes()
    {
        ShellCommand command = ShellParser
            .Parse("""helper echo "$HOME/bin" %vault:TOKEN%""")
            .Lists[0].Pipeline.Commands[0];

        Assert.Contains(command.Words[2].Parts, part => part is ShellVariable);
        Assert.Equal("%vault:TOKEN%", command.Words[3].Raw);
    }

    [Fact]
    public void Parse_ReadsPipesListsAndRedirections()
    {
        ShellScript script = ShellParser.Parse(
            "helper echo one | helper cat > out.txt && helper echo two 2>&1 ; helper echo three");

        Assert.Equal(3, script.Lists.Count);
        Assert.Equal(ShellListOperator.And, script.Lists[1].Operator);
        Assert.Equal(ShellListOperator.Sequence, script.Lists[2].Operator);
        Assert.Equal(2, script.Lists[0].Pipeline.Commands.Count);

        ShellCommand first = script.Lists[0].Pipeline.Commands[1];
        ShellRedirect redirect = Assert.Single(first.Redirects);
        Assert.Equal(ShellRedirectKind.StandardOutput, redirect.Kind);
        Assert.Equal("out.txt", redirect.Target!.Raw);

        ShellCommand second = script.Lists[1].Pipeline.Commands[0];
        Assert.Contains(second.Redirects, item =>
            item.Kind == ShellRedirectKind.MergeStandardError);
    }

    [Fact]
    public void Parse_ReadsInlineAssignments()
    {
        ShellCommand command = ShellParser
            .Parse("DOTNET_NOLOGO=1 helper echo done")
            .Lists[0].Pipeline.Commands[0];

        Assert.Equal("1", command.Assignments["DOTNET_NOLOGO"].Raw);
        Assert.Equal("helper", command.Words[0].Raw);
    }

    [Theory]
    [InlineData("echo $(whoami)")]
    [InlineData("echo `whoami`")]
    [InlineData("sleep 1 &")]
    [InlineData("helper echo *.cs")]
    [InlineData("helper echo file?.txt")]
    [InlineData("helper echo one |")]
    [InlineData("helper echo one &&")]
    [InlineData("helper echo one 2>a 2>&1")]
    [InlineData("helper echo one > a > b")]
    public void Parse_RejectsUnsupportedOrIncompleteSyntax(string text)
    {
        Assert.Throws<ShellParseException>(() => ShellParser.Parse(text));
    }

    [Fact]
    public void Parse_JoinsBackslashContinuedLines()
    {
        ShellCommand command = ShellParser
            .Parse("helper echo one \\\ntwo")
            .Lists[0].Pipeline.Commands[0];

        Assert.Equal(["helper", "echo", "one", "two"], command.Words.Select(word => word.Raw));
    }

    [Fact]
    public void Parse_AllowsQuotedGlobCharacters()
    {
        ShellCommand command = ShellParser
            .Parse("helper echo '*.cs' \"file?.txt\"")
            .Lists[0].Pipeline.Commands[0];

        Assert.Equal("*.cs", command.Words[2].Raw);
        Assert.Equal("file?.txt", command.Words[3].Raw);
    }

    [Fact]
    public void Render_ExpandsVariablesAndVaultReferences()
    {
        ShellWord word = ShellParser
            .Parse("helper echo $HOME/%vault:TOKEN%")
            .Lists[0].Pipeline.Commands[0]
            .Words[2];
        var environment = new Dictionary<string, string> { ["HOME"] = "/home/test" };
        var secrets = new TestSecretResolver(("TOKEN", "s3cret-value"));

        SecretExpansion expansion = ShellWordRenderer.Render(word, environment, secrets);

        Assert.Equal("/home/test/s3cret-value", expansion.Text);
        SecretUse use = Assert.Single(expansion.Used);
        Assert.Equal("TOKEN", use.Name);
    }

    [Fact]
    public void Render_KeepsSingleQuotedDollarsLiteral()
    {
        ShellWord word = ShellParser
            .Parse("helper echo '$HOME'")
            .Lists[0].Pipeline.Commands[0]
            .Words[2];

        SecretExpansion expansion = ShellWordRenderer.Render(
            word, new Dictionary<string, string> { ["HOME"] = "/home/test" },
            new TestSecretResolver());

        Assert.Equal("$HOME", expansion.Text);
    }
}
