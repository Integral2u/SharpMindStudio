using System.Text;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Work;

public abstract record ShellWordPart;

public sealed record ShellLiteral(string Text) : ShellWordPart;

public sealed record ShellVariable(string Name) : ShellWordPart;

/// <summary>
/// One word after quoting. A single-quoted dollar is a literal; an unquoted or
/// double-quoted dollar becomes a <see cref="ShellVariable"/> part.
/// </summary>
public sealed record ShellWord(IReadOnlyList<ShellWordPart> Parts)
{
    public string Raw
    {
        get
        {
            var text = new StringBuilder();
            foreach (ShellWordPart part in Parts)
            {
                switch (part)
                {
                    case ShellLiteral literal:
                        text.Append(literal.Text);
                        break;
                    case ShellVariable variable:
                        text.Append('$').Append(variable.Name);
                        break;
                }
            }
            return text.ToString();
        }
    }
}

public enum ShellRedirectKind
{
    StandardInput,
    StandardOutput,
    StandardOutputAppend,
    StandardError,
    StandardErrorAppend,
    MergeStandardError,
}

public sealed record ShellRedirect(ShellRedirectKind Kind, ShellWord? Target);

public sealed record ShellCommand(
    string Source,
    IReadOnlyList<ShellWord> Words,
    IReadOnlyDictionary<string, ShellWord> Assignments,
    IReadOnlyList<ShellRedirect> Redirects);

public sealed record ShellPipeline(IReadOnlyList<ShellCommand> Commands);

public enum ShellListOperator
{
    Sequence,
    And,
    Or,
}

public sealed record ShellList(ShellListOperator Operator, ShellPipeline Pipeline);

public sealed record ShellScript(IReadOnlyList<ShellList> Lists);

public sealed class ShellParseException(string message) : Exception(message);

/// <summary>Renders a parsed word against the safe environment and the Vault.</summary>
public static class ShellWordRenderer
{
    /// <summary>Expands $VAR only; %vault: references are left for the command runner.</summary>
    public static string RenderVariables(
        ShellWord word,
        IReadOnlyDictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach (ShellWordPart part in word.Parts)
        {
            switch (part)
            {
                case ShellLiteral literal:
                    text.Append(literal.Text);
                    break;
                case ShellVariable variable:
                    text.Append(environment.TryGetValue(variable.Name, out string? value)
                        ? value
                        : "");
                    break;
            }
        }
        return text.ToString();
    }

    public static SecretExpansion Render(
        ShellWord word,
        IReadOnlyDictionary<string, string> environment,
        ISecretResolver secrets) =>
        SecretTemplate.Expand(RenderVariables(word, environment), secrets.Resolve);
}
