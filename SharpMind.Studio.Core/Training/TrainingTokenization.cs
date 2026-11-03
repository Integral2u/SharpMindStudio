using SharpMind.Tokenization;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Document-boundary handling for training batches. The tokenizer owns the
/// actual end-of-text token; callers should never have to write a literal
/// <c>&lt;|endoftext|&gt;</c> into the corpus for the model to learn a boundary.
/// </summary>
public static class TrainingTokenization
{
    /// <summary>
    /// Encodes one document and optionally appends the tokenizer's EOS id.
    /// Use this for batchers that concatenate documents without inserting a
    /// boundary themselves (currently <c>RandomWindowBatcher</c>);
    /// <c>PackingBatcher</c> already inserts EOS and should use plain Encode.
    /// </summary>
    public static int[] EncodeDocument(Tokenizer tokenizer, string text, bool appendEos)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        int[] tokens = tokenizer.Encode(text);
        return appendEos ? AppendBoundary(tokens, tokenizer.EosId) : tokens;
    }

    /// <summary>
    /// Appends <paramref name="eosId"/> unless it is already the final token.
    /// Kept separate so the boundary rule is testable without a real tokenizer.
    /// </summary>
    public static int[] AppendBoundary(int[] tokens, int eosId)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Length > 0 && tokens[^1] == eosId) return tokens;

        int[] result = new int[tokens.Length + 1];
        tokens.CopyTo(result, 0);
        result[^1] = eosId;
        return result;
    }
}
