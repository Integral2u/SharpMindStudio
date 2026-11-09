using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.Chat;

/// <summary>
/// Turns a Markdown reply into the prose that text-to-speech should read: code blocks,
/// links and emphasis markers are dropped, list markers become plain sentences.
/// </summary>
public static class SpeechText
{
    public static string ToSpeech(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var text = new StringBuilder(markdown.Length);
        bool inFence = false;
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            line = line.TrimStart();
            if (line.StartsWith('#') || line.StartsWith('>')) line = line.TrimStart('#', '>', ' ');
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                line = line[2..];
            text.Append(line).Append('\n');
        }

        string speech = text.ToString();
        speech = Regex.Replace(speech, @"\[([^\]]*)\]\([^)]*\)", "$1");  // links -> label
        speech = speech.Replace("`", "").Replace("**", "").Replace("__", "");
        speech = speech.Replace("*", "");
        speech = Regex.Replace(speech, @"[ \t]+", " ");
        speech = Regex.Replace(speech, @"\n{2,}", "\n");
        speech = speech.Trim();
        // A punctuation-only reply (the LFM3-Audio backbone's bare ":") is not
        // speech: it produced a 0.16 s WAV the player showed as 0:00. Treat it as
        // nothing to say rather than synthesizing a click.
        return speech.Any(char.IsLetterOrDigit) ? speech : "";
    }
}
