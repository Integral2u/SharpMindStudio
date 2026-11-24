namespace SharpMind.Studio.Core.Chat;

/// <summary>Recognises the trailing <c>@token</c> in a chat composer.</summary>
public static class ChatContextParser
{
    /// <summary>
    /// Returns the text after a trailing <c>@</c> when that token begins at the
    /// start of the input or after whitespace. An email address such as
    /// <c>name@example.com</c> or a completed token followed by a space is not a
    /// context reference.
    /// </summary>
    public static bool TryGetToken(string? text, out string query)
    {
        query = "";
        if (string.IsNullOrEmpty(text)) return false;

        int start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start >= text.Length) return false;

        string token = text[start..];
        if (token[0] != '@') return false;
        query = token[1..];
        return true;
    }
}
