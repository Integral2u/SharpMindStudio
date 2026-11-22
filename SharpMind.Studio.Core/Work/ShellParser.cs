using System.Text;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Parses the documented POSIX-style subset: simple commands, quotes, pipes,
/// redirections, &amp;&amp;/||/; and inline assignments. There is no real shell;
/// unsupported constructs fail with a readable message.
/// </summary>
public static class ShellParser
{
    private static readonly Regex VariableName = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex AssignmentName = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    public static ShellScript Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<Token> tokens = Tokenize(text);
        if (tokens.Count == 0)
            throw new ShellParseException("A command is required.");

        var lists = new List<ShellList>();
        ShellListOperator listOperator = ShellListOperator.Sequence;
        int index = 0;

        while (index < tokens.Count && tokens[index].Kind == TokenKind.Semicolon)
            index++;

        while (index < tokens.Count)
        {
            ShellPipeline pipeline = ParsePipeline(tokens, ref index);
            lists.Add(new ShellList(listOperator, pipeline));

            if (index >= tokens.Count) break;
            switch (tokens[index].Kind)
            {
                case TokenKind.Semicolon:
                    listOperator = ShellListOperator.Sequence;
                    index++;
                    break;
                case TokenKind.And:
                    listOperator = ShellListOperator.And;
                    index++;
                    if (index >= tokens.Count)
                        throw new ShellParseException("A command is required after '&&'.");
                    break;
                case TokenKind.Or:
                    listOperator = ShellListOperator.Or;
                    index++;
                    if (index >= tokens.Count)
                        throw new ShellParseException("A command is required after '||'.");
                    break;
                default:
                    throw new ShellParseException(
                        $"Unexpected '{tokens[index].Raw}' after a command.");
            }
        }

        if (lists.Count == 0)
            throw new ShellParseException("A command is required.");
        return new ShellScript(lists);
    }

    private static ShellPipeline ParsePipeline(List<Token> tokens, ref int index)
    {
        var commands = new List<ShellCommand>();
        commands.Add(ParseCommand(tokens, ref index));

        while (index < tokens.Count && tokens[index].Kind == TokenKind.Pipe)
        {
            index++;
            if (index >= tokens.Count)
                throw new ShellParseException("A command is required after '|'.");
            commands.Add(ParseCommand(tokens, ref index));
        }

        return new ShellPipeline(commands);
    }

    private static ShellCommand ParseCommand(List<Token> tokens, ref int index)
    {
        var words = new List<ShellWord>();
        var assignments = new Dictionary<string, ShellWord>(StringComparer.OrdinalIgnoreCase);
        var redirects = new List<ShellRedirect>();
        var source = new StringBuilder();

        while (index < tokens.Count && !IsSeparator(tokens[index].Kind))
        {
            Token token = tokens[index];
            if (token.Kind == TokenKind.Word)
            {
                if (words.Count == 0 && TryAssignment(token.Word!, out string? name, out ShellWord? value))
                {
                    assignments[name] = value;
                }
                else
                {
                    words.Add(token.Word!);
                }

                source.Append(token.Word!.Raw).Append(' ');
                index++;
                continue;
            }

            if (IsRedirect(token.Kind))
            {
                ShellRedirect redirect = ParseRedirect(tokens, ref index);
                redirects.Add(redirect);
                source.Append(token.Raw).Append(' ');
                if (redirect.Target is not null)
                    source.Append(redirect.Target.Raw).Append(' ');
                continue;
            }

            throw new ShellParseException($"Unexpected '{token.Raw}'.");
        }

        if (words.Count == 0)
            throw new ShellParseException("A command is required after the assignments.");

        ValidateRedirects(redirects);
        return new ShellCommand(
            source.ToString().Trim(),
            words,
            assignments,
            redirects);
    }

    private static ShellRedirect ParseRedirect(List<Token> tokens, ref int index)
    {
        Token token = tokens[index++];
        ShellRedirectKind kind = token.Kind switch
        {
            TokenKind.RedirectIn => ShellRedirectKind.StandardInput,
            TokenKind.RedirectOut => ShellRedirectKind.StandardOutput,
            TokenKind.RedirectAppend => ShellRedirectKind.StandardOutputAppend,
            TokenKind.RedirectErr => ShellRedirectKind.StandardError,
            TokenKind.RedirectErrAppend => ShellRedirectKind.StandardErrorAppend,
            TokenKind.MergeErr => ShellRedirectKind.MergeStandardError,
            _ => throw new ShellParseException($"Unexpected redirect '{token.Raw}'."),
        };

        if (kind == ShellRedirectKind.MergeStandardError)
            return new ShellRedirect(kind, null);

        if (index >= tokens.Count || tokens[index].Kind != TokenKind.Word)
            throw new ShellParseException($"'{token.Raw}' needs a file name.");

        ShellWord target = tokens[index++].Word!;
        return new ShellRedirect(kind, target);
    }

    private static void ValidateRedirects(IReadOnlyList<ShellRedirect> redirects)
    {
        if (redirects.Count(redirect => redirect.Kind is
                ShellRedirectKind.StandardOutput or ShellRedirectKind.StandardOutputAppend) > 1)
        {
            throw new ShellParseException("A command can redirect stdout only once.");
        }

        if (redirects.Count(redirect => redirect.Kind is
                ShellRedirectKind.StandardError or ShellRedirectKind.StandardErrorAppend) > 1)
        {
            throw new ShellParseException("A command can redirect stderr only once.");
        }

        if (redirects.Count(redirect => redirect.Kind == ShellRedirectKind.StandardInput) > 1)
            throw new ShellParseException("A command can redirect stdin only once.");

        bool merge = redirects.Any(redirect => redirect.Kind == ShellRedirectKind.MergeStandardError);
        bool stderr = redirects.Any(redirect => redirect.Kind is
            ShellRedirectKind.StandardError or ShellRedirectKind.StandardErrorAppend);
        if (merge && stderr)
            throw new ShellParseException("Use either '2>file' or '2>&1', not both.");
    }

    private static bool TryAssignment(
        ShellWord word,
        out string name,
        out ShellWord value)
    {
        name = "";
        value = new ShellWord([]);

        if (word.Parts.Count == 0 || word.Parts[0] is not ShellLiteral first)
            return false;

        int equals = first.Text.IndexOf('=');
        if (equals <= 0) return false;

        string candidate = first.Text[..equals];
        if (!AssignmentName.IsMatch(candidate)) return false;

        var parts = new List<ShellWordPart>();
        string rest = first.Text[(equals + 1)..];
        if (rest.Length > 0) parts.Add(new ShellLiteral(rest));
        parts.AddRange(word.Parts.Skip(1));

        name = candidate;
        value = new ShellWord(parts);
        return true;
    }

    private static bool IsSeparator(TokenKind kind) =>
        kind is TokenKind.Pipe or TokenKind.And or TokenKind.Or or TokenKind.Semicolon;

    private static bool IsRedirect(TokenKind kind) =>
        kind is TokenKind.RedirectIn or TokenKind.RedirectOut or TokenKind.RedirectAppend
            or TokenKind.RedirectErr or TokenKind.RedirectErrAppend or TokenKind.MergeErr;

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var parts = new List<ShellWordPart>();
        var literal = new StringBuilder();
        bool wordStarted = false;
        bool single = false;
        bool quoted = false;

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            parts.Add(new ShellLiteral(literal.ToString()));
            literal.Clear();
        }

        void FlushWord()
        {
            if (!wordStarted) return;
            FlushLiteral();
            if (parts.Count == 0) parts.Add(new ShellLiteral(""));
            tokens.Add(new Token(TokenKind.Word, new ShellWord([.. parts]), ""));
            parts.Clear();
            wordStarted = false;
        }

        void AppendLiteral(string value)
        {
            literal.Append(value);
            wordStarted = true;
        }

        void AppendVariable(string name)
        {
            FlushLiteral();
            parts.Add(new ShellVariable(name));
            wordStarted = true;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];

            if (single)
            {
                if (character == '\'') single = false;
                else AppendLiteral(character.ToString());
                continue;
            }

            if (quoted)
            {
                if (character == '"')
                {
                    quoted = false;
                    continue;
                }

                if (character == '\\' && i + 1 < text.Length &&
                    text[i + 1] is '"' or '\\' or '$')
                {
                    AppendLiteral(text[++i].ToString());
                    continue;
                }

                if (character == '$' && TryVariable(text, ref i, out string? doubleName))
                {
                    AppendVariable(doubleName);
                    continue;
                }

                AppendLiteral(character.ToString());
                continue;
            }

            if (character == '\'')
            {
                single = true;
                wordStarted = true;
                continue;
            }

            if (character == '"')
            {
                quoted = true;
                wordStarted = true;
                continue;
            }

            if (character == '\\')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                    continue;
                }

                if (i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n')
                {
                    i += 2;
                    continue;
                }

                if (i + 1 < text.Length && IsEscapable(text[i + 1]))
                {
                    AppendLiteral(text[++i].ToString());
                    continue;
                }

                AppendLiteral("\\");
                continue;
            }

            if (character == '`')
                throw new ShellParseException("Command substitution with backticks is not supported.");

            if (character == '$')
            {
                if (i + 1 < text.Length && text[i + 1] == '(')
                    throw new ShellParseException("Command substitution with $() is not supported.");

                if (TryVariable(text, ref i, out string? name))
                {
                    AppendVariable(name);
                    continue;
                }

                AppendLiteral("$");
                continue;
            }

            if (character == '#')
            {
                if (!wordStarted)
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }

                AppendLiteral("#");
                continue;
            }

            if (character is '*' or '?' or '[')
            {
                throw new ShellParseException(
                    $"Globbing with '{character}' is not supported; quote the argument to pass it literally.");
            }

            if (char.IsWhiteSpace(character))
            {
                FlushWord();
                if (character == '\n')
                    tokens.Add(new Token(TokenKind.Semicolon, null, ";"));
                continue;
            }

            if (character == '|')
            {
                FlushWord();
                if (i + 1 < text.Length && text[i + 1] == '|')
                {
                    tokens.Add(new Token(TokenKind.Or, null, "||"));
                    i++;
                }
                else
                {
                    tokens.Add(new Token(TokenKind.Pipe, null, "|"));
                }
                continue;
            }

            if (character == '&')
            {
                FlushWord();
                if (i + 1 < text.Length && text[i + 1] == '&')
                {
                    tokens.Add(new Token(TokenKind.And, null, "&&"));
                    i++;
                }
                else
                {
                    throw new ShellParseException("Background jobs with '&' are not supported.");
                }
                continue;
            }

            if (character == ';')
            {
                FlushWord();
                tokens.Add(new Token(TokenKind.Semicolon, null, ";"));
                continue;
            }

            if (character == '<')
            {
                FlushWord();
                if (i + 1 < text.Length && text[i + 1] is '<' or '(')
                    throw new ShellParseException("Here-docs and process substitution are not supported.");
                tokens.Add(new Token(TokenKind.RedirectIn, null, "<"));
                continue;
            }

            if (character == '>')
            {
                FlushWord();
                if (i + 1 < text.Length && text[i + 1] == '>')
                {
                    tokens.Add(new Token(TokenKind.RedirectAppend, null, ">>"));
                    i++;
                }
                else
                {
                    tokens.Add(new Token(TokenKind.RedirectOut, null, ">"));
                }
                continue;
            }

            if (character == '2' && !wordStarted &&
                i + 1 < text.Length && text[i + 1] == '>')
            {
                if (i + 3 < text.Length && text[i + 2] == '&' && text[i + 3] == '1')
                {
                    tokens.Add(new Token(TokenKind.MergeErr, null, "2>&1"));
                    i += 3;
                    continue;
                }

                if (i + 2 < text.Length && text[i + 2] == '>')
                {
                    tokens.Add(new Token(TokenKind.RedirectErrAppend, null, "2>>"));
                    i += 2;
                    continue;
                }

                tokens.Add(new Token(TokenKind.RedirectErr, null, "2>"));
                i += 1;
                continue;
            }

            AppendLiteral(character.ToString());
        }

        FlushWord();
        return tokens;
    }

    private static bool TryVariable(string text, ref int index, out string name)
    {
        name = "";
        int start = index + 1;
        if (start >= text.Length) return false;

        if (text[start] == '{')
        {
            int close = text.IndexOf('}', start + 1);
            if (close < 0)
                throw new ShellParseException("Unclosed '${...}' variable.");
            string candidate = text[(start + 1)..close];
            if (!VariableName.IsMatch(candidate))
                throw new ShellParseException($"Invalid variable name '${{{candidate}}}'.");
            name = candidate;
            index = close;
            return true;
        }

        if (text[start] == '?')
        {
            name = "?";
            index = start;
            return true;
        }

        if (!char.IsLetter(text[start]) && text[start] != '_')
            return false;

        int end = start + 1;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            end++;

        name = text[start..end];
        index = end - 1;
        return true;
    }

    private static bool IsEscapable(char character) =>
        char.IsWhiteSpace(character) || character is '"' or '\'' or '\\' or '$'
            or '|' or '&' or ';' or '<' or '>' or '(' or ')';

    private enum TokenKind
    {
        Word,
        Pipe,
        And,
        Or,
        Semicolon,
        RedirectIn,
        RedirectOut,
        RedirectAppend,
        RedirectErr,
        RedirectErrAppend,
        MergeErr,
    }

    private sealed record Token(TokenKind Kind, ShellWord? Word, string Raw);
}
